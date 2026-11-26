using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Msil;

/// <summary>Assembles an <see cref="IrModule"/> into a verifiable .NET PE/CLI binary and optional runtime configuration.</summary>
internal static class MsilAssemblyBuilder {

  /// <summary>Builds the generated files for <paramref name="module"/>.</summary>
  public static IReadOnlyList<GeneratedFile> Build(IrModule module, MsilProfile profile, MsilEmitOptions options) {
    var outputKind = options.OutputKind ?? profile.OutputKind;
    var isExe = outputKind == MsilOutputKind.ConsoleExe;
    var asmName = SanitizeName(options.AssemblyName is { Length: > 0 } custom ? custom : module.Name);
    var className = SanitizeName(module.Name);

    var metadata = new MetadataBuilder();
    var mvid = DeterministicGuid(asmName);
    metadata.AddModule(0, metadata.GetOrAddString($"{asmName}.dll"), metadata.GetOrAddGuid(mvid), default, default);
    metadata.AddAssembly(metadata.GetOrAddString(asmName), new Version(1, 0, 0, 0), default, default, 0, AssemblyHashAlgorithm.None);

    var ctx = new MsilMetadataContext(metadata, profile);

    // Row 1: <Module>
    var nextFieldRow = 1;
    var nextMethodRow = 1;
    metadata.AddTypeDefinition(
      default,
      default,
      metadata.GetOrAddString("<Module>"),
      default,
      MetadataTokens.FieldDefinitionHandle(nextFieldRow),
      MetadataTokens.MethodDefinitionHandle(nextMethodRow));

    // Pre-assign TypeDefinitionHandles for declared enums and structs so fields can reference each other in any order.
    var enums = module.Types.OfType<IrEnumType>().ToList();
    var structs = module.Types.OfType<IrStructType>().ToList();
    var nextTypeRow = 2;
    foreach (var enumType in enums)
      ctx.DeclaredTypes[enumType] = MetadataTokens.TypeDefinitionHandle(nextTypeRow++);
    foreach (var structType in structs)
      ctx.DeclaredTypes[structType] = MetadataTokens.TypeDefinitionHandle(nextTypeRow++);

    // Emit declared enums.
    var systemEnum = ctx.GetBclType("System", "Enum");
    foreach (var enumType in enums) {
      metadata.AddTypeDefinition(
        TypeAttributes.Public | TypeAttributes.Sealed,
        default,
        metadata.GetOrAddString(SanitizeName(enumType.Name)),
        systemEnum,
        MetadataTokens.FieldDefinitionHandle(nextFieldRow),
        MetadataTokens.MethodDefinitionHandle(nextMethodRow));

      metadata.AddFieldDefinition(
        FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName,
        metadata.GetOrAddString("value__"),
        ctx.EncodeFieldSignature(enumType.Underlying));
      nextFieldRow++;

      var enumSig = ctx.EncodeFieldSignature(enumType.Type);
      foreach (var member in enumType.Members) {
        var fieldHandle = metadata.AddFieldDefinition(
          FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault,
          metadata.GetOrAddString(member.Name),
          enumSig);
        metadata.AddConstant(fieldHandle, BoxEnumConstant(enumType.Underlying, member.Value));
        nextFieldRow++;
      }
    }

    // Emit declared structs.
    var systemValueType = ctx.GetBclType("System", "ValueType");
    foreach (var structType in structs) {
      var layout = structType.Layout;
      var layoutAttr = layout?.Kind == IrLayoutKind.Explicit ? TypeAttributes.ExplicitLayout : TypeAttributes.SequentialLayout;
      var structHandle = metadata.AddTypeDefinition(
        TypeAttributes.Public | layoutAttr | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
        default,
        metadata.GetOrAddString(SanitizeName(structType.Name)),
        systemValueType,
        MetadataTokens.FieldDefinitionHandle(nextFieldRow),
        MetadataTokens.MethodDefinitionHandle(nextMethodRow));

      if (layout is not null && (layout.Pack > 0 || layout.Size > 0))
        metadata.AddTypeLayout(structHandle, (ushort)layout.Pack, (uint)layout.Size);

      for (var i = 0; i < structType.Fields.Count; i++) {
        var field = structType.Fields[i];
        var fieldHandle = metadata.AddFieldDefinition(
          FieldAttributes.Public,
          metadata.GetOrAddString(field.Name),
          ctx.EncodeFieldSignature(field.Type));
        if (layout is { Kind: IrLayoutKind.Explicit })
          metadata.AddFieldLayout(fieldHandle, layout.Offsets[i]);
        ctx.StructFields[field] = fieldHandle;
        nextFieldRow++;
      }
    }

    // Emit the module static class.
    var needsCctor = module.Constants.Count > 0 || module.Globals.Count > 0 || module.Initializer is not null;
    var classFlags = TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed;
    if (!needsCctor)
      classFlags |= TypeAttributes.BeforeFieldInit;

    metadata.AddTypeDefinition(
      classFlags,
      default,
      metadata.GetOrAddString(className),
      ctx.GetBclType("System", "Object"),
      MetadataTokens.FieldDefinitionHandle(nextFieldRow),
      MetadataTokens.MethodDefinitionHandle(nextMethodRow));

    // Static fields for module constants and globals.
    foreach (var constant in module.Constants) {
      var handle = metadata.AddFieldDefinition(
        FieldAttributes.Assembly | FieldAttributes.Static | FieldAttributes.InitOnly,
        metadata.GetOrAddString($"$const_{constant.Name}"),
        ctx.EncodeFieldSignature(constant.Type));
      ctx.ConstantFields[constant] = handle;
      nextFieldRow++;
    }

    foreach (var global in module.Globals) {
      var handle = metadata.AddFieldDefinition(
        FieldAttributes.Public | FieldAttributes.Static,
        metadata.GetOrAddString(global.Name),
        ctx.EncodeFieldSignature(global.Type));
      ctx.GlobalFields[global] = handle;
      nextFieldRow++;
    }

    // Pre-assign method handles for .cctor, every IR function, foreign P/Invoke declarations, and the console entry point.
    var methodRow = nextMethodRow;
    if (needsCctor)
      methodRow++;

    foreach (var fn in module.Functions) {
      ctx.FunctionHandles[fn.Name] = MetadataTokens.MethodDefinitionHandle(methodRow++);
      ctx.FunctionsByName[fn.Name] = fn;
    }

    foreach (var foreign in module.ForeignFunctions) {
      ctx.ForeignHandles[foreign.Name] = MetadataTokens.MethodDefinitionHandle(methodRow++);
      ctx.ForeignByName[foreign.Name] = foreign;
    }

    var ilStream = new BlobBuilder();
    var methodBodies = new MethodBodyStreamEncoder(ilStream);
    var nextParamRow = 1;

    // Emit .cctor if needed.
    if (needsCctor) {
      var cctorOffset = MsilMethodCompiler.CompileStaticCtor(module, ctx, methodBodies);
      metadata.AddMethodDefinition(
        MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
        MethodImplAttributes.IL,
        metadata.GetOrAddString(".cctor"),
        ctx.EncodeMethodSignature(IrType.Void, []),
        cctorOffset,
        MetadataTokens.ParameterHandle(nextParamRow));
    }

    // Emit every IR function.
    foreach (var fn in module.Functions) {
      var bodyOffset = MsilMethodCompiler.CompileFunction(fn, ctx, methodBodies);
      var paramTypes = fn.Parameters.Select(p => p.Type).ToArray();
      var attributes = (fn.IsPublic ? MethodAttributes.Public : MethodAttributes.Assembly)
        | MethodAttributes.Static | MethodAttributes.HideBySig;

      metadata.AddMethodDefinition(
        attributes,
        MethodImplAttributes.IL,
        metadata.GetOrAddString(fn.Name),
        ctx.EncodeMethodSignature(fn.ReturnType, paramTypes),
        bodyOffset,
        MetadataTokens.ParameterHandle(nextParamRow));

      for (var i = 0; i < fn.Parameters.Count; i++) {
        metadata.AddParameter(ParameterAttributes.None, metadata.GetOrAddString(fn.Parameters[i].Name), i + 1);
        nextParamRow++;
      }
    }

    // Emit every P/Invoke foreign declaration (RVA = 0 via bodyOffset: -1).
    foreach (var foreign in module.ForeignFunctions) {
      var attributes = (foreign.IsPublic ? MethodAttributes.Public : MethodAttributes.Private)
        | MethodAttributes.Static | MethodAttributes.HideBySig | MethodAttributes.PinvokeImpl;

      var methodHandle = metadata.AddMethodDefinition(
        attributes,
        MethodImplAttributes.PreserveSig,
        metadata.GetOrAddString(foreign.Name),
        ctx.EncodeForeignMethodSignature(foreign),
        bodyOffset: -1,
        MetadataTokens.ParameterHandle(nextParamRow));

      if (foreign.ReturnType != IrType.Void && TryGetMarshallingBlob(foreign.Result, foreign.CharSet, out var retMarshal)) {
        var retParam = metadata.AddParameter(ParameterAttributes.HasFieldMarshal, default, 0);
        metadata.AddMarshallingDescriptor(retParam, metadata.GetOrAddBlob(retMarshal));
        nextParamRow++;
      }

      for (var i = 0; i < foreign.Parameters.Count; i++) {
        var param = foreign.Parameters[i];
        var paramAttrs = MapForeignParameterAttributes(param.Marshal);
        var hasMarshal = TryGetMarshallingBlob(param.Marshal, foreign.CharSet, out var paramMarshal);
        if (hasMarshal)
          paramAttrs |= ParameterAttributes.HasFieldMarshal;

        var paramHandle = metadata.AddParameter(paramAttrs, metadata.GetOrAddString(param.Name), i + 1);
        if (hasMarshal)
          metadata.AddMarshallingDescriptor(paramHandle, metadata.GetOrAddBlob(paramMarshal));
        nextParamRow++;
      }

      metadata.AddMethodImport(
        methodHandle,
        MapMethodImportAttributes(foreign),
        metadata.GetOrAddString(foreign.Symbol),
        ctx.GetModuleRef(foreign.Library));
    }

    // Emit CLI entry point for ConsoleExe.
    MethodDefinitionHandle entryPoint = default;
    if (isExe) {
      var entryCandidate = FindEntryPointCandidate(module);
      var entryOffset = MsilMethodCompiler.CompileEntryPoint(entryCandidate, ctx, methodBodies);
      entryPoint = metadata.AddMethodDefinition(
        MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
        MethodImplAttributes.IL,
        metadata.GetOrAddString("<Main>$"),
        ctx.EncodeMethodSignature(IrType.I32, [IrType.Array(IrType.String)]),
        entryOffset,
        MetadataTokens.ParameterHandle(nextParamRow));
      metadata.AddParameter(ParameterAttributes.None, metadata.GetOrAddString("args"), 1);
    }

    // Serialize PE image.
    var peHeader = isExe ? PEHeaderBuilder.CreateExecutableHeader() : PEHeaderBuilder.CreateLibraryHeader();
    var peBuilder = new ManagedPEBuilder(
      header: peHeader,
      metadataRootBuilder: new MetadataRootBuilder(metadata),
      ilStream: ilStream,
      entryPoint: entryPoint,
      flags: CorFlags.ILOnly,
      deterministicIdProvider: _ => new BlobContentId(mvid, 0x04030201));

    var peBlob = new BlobBuilder();
    peBuilder.Serialize(peBlob);
    var dllFile = new GeneratedFile($"{asmName}.dll", "") { BinaryContent = peBlob.ToArray() };

    if (!isExe)
      return [dllFile];

    var runtimeConfig = $$"""
      {
        "runtimeOptions": {
          "tfm": "{{profile.TargetFramework}}",
          "framework": {
            "name": "Microsoft.NETCore.App",
            "version": "{{profile.RuntimeFrameworkVersion}}"
          }
        }
      }
      """;
    return [dllFile, new GeneratedFile($"{asmName}.runtimeconfig.json", runtimeConfig + "\n")];
  }

  /// <summary>Selects an entry-point candidate function (<c>Main</c> or <c>main</c>) if present.</summary>
  private static IrFunction? FindEntryPointCandidate(IrModule module) {
    static bool IsEligible(IrFunction fn) {
      var validParams = fn.Parameters.Count == 0
        || (fn.Parameters.Count == 1 && fn.Parameters[0].Type == IrType.Array(IrType.String));
      var validReturn = fn.ReturnType == IrType.Void
        || fn.ReturnType == IrType.I32
        || fn.ReturnType == IrType.I64
        || fn.ReturnType == IrType.U32
        || fn.ReturnType == IrType.U64;
      return validParams && validReturn;
    }

    return module.Functions.FirstOrDefault(fn => fn.Name == "Main" && IsEligible(fn))
      ?? module.Functions.FirstOrDefault(fn => string.Equals(fn.Name, "main", StringComparison.OrdinalIgnoreCase) && IsEligible(fn));
  }

  private static MethodImportAttributes MapMethodImportAttributes(IrForeignFunction foreign) {
    var attrs = foreign.Convention switch {
      IrCallingConvention.Cdecl => MethodImportAttributes.CallingConventionCDecl,
      IrCallingConvention.StdCall => MethodImportAttributes.CallingConventionStdCall,
      IrCallingConvention.ThisCall => MethodImportAttributes.CallingConventionThisCall,
      IrCallingConvention.FastCall => MethodImportAttributes.CallingConventionFastCall,
      _ => MethodImportAttributes.CallingConventionWinApi
    };

    attrs |= foreign.CharSet switch {
      IrStringEncoding.Ansi => MethodImportAttributes.CharSetAnsi,
      IrStringEncoding.Unicode => MethodImportAttributes.CharSetUnicode,
      _ => MethodImportAttributes.None
    };

    if (foreign.SetLastError == true)
      attrs |= MethodImportAttributes.SetLastError;
    if (foreign.ExactSpelling == true)
      attrs |= MethodImportAttributes.ExactSpelling;

    return attrs;
  }

  private static ParameterAttributes MapForeignParameterAttributes(IrMarshal marshal) {
    if (marshal.Kind is not (IrMarshalKind.Array or IrMarshalKind.ByRef))
      return ParameterAttributes.None;

    return marshal.Direction switch {
      IrMarshalDirection.In => ParameterAttributes.In,
      IrMarshalDirection.Out => ParameterAttributes.Out,
      _ => ParameterAttributes.In | ParameterAttributes.Out
    };
  }

  private static bool TryGetMarshallingBlob(IrMarshal marshal, IrStringEncoding? charSet, out byte[] blob) {
    if (marshal.Kind == IrMarshalKind.Bool && marshal.BoolForm != IrBoolForm.Bool) {
      blob = [marshal.BoolForm == IrBoolForm.U1 ? (byte)0x06 : (byte)0x05];
      return true;
    }

    if (marshal.Kind == IrMarshalKind.String) {
      if (marshal.Encoding == IrStringEncoding.Utf8) {
        blob = [0x30]; // UnmanagedType.LPUTF8Str
        return true;
      }

      if (marshal.Encoding == IrStringEncoding.Unicode && charSet != IrStringEncoding.Unicode) {
        blob = [0x15]; // UnmanagedType.LPWStr
        return true;
      }

      if (marshal.Encoding == IrStringEncoding.Ansi && charSet == IrStringEncoding.Unicode) {
        blob = [0x14]; // UnmanagedType.LPStr
        return true;
      }
    }

    blob = [];
    return false;
  }

  private static object BoxEnumConstant(IrType underlying, long value) => underlying.Kind switch {
    IrTypeKind.I8 => unchecked((sbyte)value),
    IrTypeKind.U8 => unchecked((byte)value),
    IrTypeKind.I16 => unchecked((short)value),
    IrTypeKind.U16 => unchecked((ushort)value),
    IrTypeKind.I32 => unchecked((int)value),
    IrTypeKind.U32 => unchecked((uint)value),
    IrTypeKind.I64 => value,
    _ => unchecked((ulong)value)
  };

  private static string SanitizeName(string name) {
    var sb = new StringBuilder(name.Length);
    foreach (var ch in name)
      sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
    if (sb.Length == 0 || char.IsDigit(sb[0]))
      sb.Insert(0, '_');
    return sb.ToString();
  }

  private static Guid DeterministicGuid(string seed) {
    var hash = SHA256.HashData(Encoding.UTF8.GetBytes("polypiler-msil:" + seed));
    return new Guid(hash.AsSpan(0, 16));
  }

}
