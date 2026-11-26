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

    var plan = MsilSynthesisPlan.Analyze(module);
    var ctx = new MsilMetadataContext(metadata, profile, plan);

    foreach (var fn in module.Functions)
      ctx.FunctionsByName[fn.Name] = fn;
    foreach (var foreign in module.ForeignFunctions)
      ctx.ForeignByName[foreign.Name] = foreign;

    var enums = module.Types.OfType<IrEnumType>().ToList();
    var structs = module.Types.OfType<IrStructType>().ToList();
    var interfaces = module.Types.OfType<IrInterfaceType>().Where(i => !i.IsDisposable).ToList();
    var classes = module.Types.OfType<IrClassType>().ToList();
    var freeFunctions = module.Functions.Where(f => !plan.MemberFunctions.Contains(f.Name)).ToList();
    var needsCctor = module.Constants.Count > 0 || module.Globals.Count > 0 || module.Initializer is not null;

    // Pass 1: Pre-assign every TypeDefinition, FieldDefinition, and MethodDefinition handle in exact emission order.
    PreAssignHandles(module, plan, ctx, enums, structs, interfaces, classes, freeFunctions, needsCctor);

    var ilStream = new BlobBuilder();
    var methodBodies = new MethodBodyStreamEncoder(ilStream);
    var nextFieldRow = 1;
    var nextMethodRow = 1;
    var nextParamRow = 1;

    // Row 1: <Module>
    metadata.AddTypeDefinition(
      default,
      default,
      metadata.GetOrAddString("<Module>"),
      default,
      MetadataTokens.FieldDefinitionHandle(nextFieldRow),
      MetadataTokens.MethodDefinitionHandle(nextMethodRow));

    // Pass 2: Emit all TypeDefinitions, FieldDefinitions, MethodDefinitions, and Parameters in exact Pass 1 order.
    EmitEnums(metadata, ctx, enums, ref nextFieldRow, nextMethodRow);
    EmitStructs(metadata, ctx, structs, ref nextFieldRow, nextMethodRow);
    EmitInterfaces(metadata, ctx, interfaces, nextFieldRow, ref nextMethodRow, ref nextParamRow);
    EmitClasses(metadata, ctx, classes, methodBodies, ref nextFieldRow, ref nextMethodRow, ref nextParamRow);
    EmitSynthesizedTypes(metadata, plan, ctx, methodBodies, ref nextFieldRow, ref nextMethodRow, ref nextParamRow);
    var entryPoint = EmitModuleClass(metadata, module, plan, ctx, className, freeFunctions, needsCctor, isExe, methodBodies, ref nextFieldRow, ref nextMethodRow, ref nextParamRow);

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

  private static void PreAssignHandles(
    IrModule module,
    MsilSynthesisPlan plan,
    MsilMetadataContext ctx,
    List<IrEnumType> enums,
    List<IrStructType> structs,
    List<IrInterfaceType> interfaces,
    List<IrClassType> classes,
    List<IrFunction> freeFunctions,
    bool needsCctor) {
    var typeRow = 2; // Row 1 is <Module>
    var fieldRow = 1;
    var methodRow = 1;

    foreach (var enumType in enums) {
      ctx.DeclaredTypes[enumType] = MetadataTokens.TypeDefinitionHandle(typeRow++);
      fieldRow += 1 + enumType.Members.Count;
    }

    foreach (var structType in structs) {
      ctx.DeclaredTypes[structType] = MetadataTokens.TypeDefinitionHandle(typeRow++);
      foreach (var field in structType.Fields)
        ctx.FieldHandles[field] = MetadataTokens.FieldDefinitionHandle(fieldRow++);
    }

    foreach (var iface in interfaces) {
      ctx.DeclaredTypes[iface] = MetadataTokens.TypeDefinitionHandle(typeRow++);
      foreach (var method in iface.Methods)
        ctx.ClassMethodHandles[method] = MetadataTokens.MethodDefinitionHandle(methodRow++);
    }

    foreach (var cls in classes) {
      ctx.DeclaredTypes[cls] = MetadataTokens.TypeDefinitionHandle(typeRow++);
      foreach (var field in cls.Fields)
        ctx.FieldHandles[field] = MetadataTokens.FieldDefinitionHandle(fieldRow++);
      foreach (var ctor in cls.Constructors) {
        var h = MetadataTokens.MethodDefinitionHandle(methodRow++);
        ctx.ClassCtorHandles[ctor] = h;
        ctx.ClassCtorByFn[ctor.Function] = h;
      }
      foreach (var method in cls.Methods)
        ctx.ClassMethodHandles[method] = MetadataTokens.MethodDefinitionHandle(methodRow++);
    }

    foreach (var cellType in plan.CellTypes) {
      ctx.CellTypes[cellType] = new(
        MetadataTokens.TypeDefinitionHandle(typeRow++),
        MetadataTokens.FieldDefinitionHandle(fieldRow++),
        MetadataTokens.MethodDefinitionHandle(methodRow++));
    }

    foreach (var fnType in plan.FunctionTypes) {
      ctx.FnInterfaces[fnType] = new(
        MetadataTokens.TypeDefinitionHandle(typeRow++),
        MetadataTokens.MethodDefinitionHandle(methodRow++));
    }

    foreach (var lambda in plan.Lambdas) {
      var td = MetadataTokens.TypeDefinitionHandle(typeRow++);
      var capFields = new FieldDefinitionHandle[lambda.Captures.Count];
      for (var i = 0; i < capFields.Length; i++)
        capFields[i] = MetadataTokens.FieldDefinitionHandle(fieldRow++);
      var ctor = MetadataTokens.MethodDefinitionHandle(methodRow++);
      var invoke = MetadataTokens.MethodDefinitionHandle(methodRow++);
      ctx.LambdaClasses[lambda] = new(td, capFields, ctor, invoke);
    }

    foreach (var fnRef in plan.FunctionRefs) {
      ctx.FnRefClasses[fnRef] = new(
        MetadataTokens.TypeDefinitionHandle(typeRow++),
        MetadataTokens.MethodDefinitionHandle(methodRow++),
        MetadataTokens.MethodDefinitionHandle(methodRow++));
    }

    foreach (var bound in plan.BoundMethods) {
      var td = MetadataTokens.TypeDefinitionHandle(typeRow++);
      FieldDefinitionHandle? recvField = bound.Method.IsStatic ? null : MetadataTokens.FieldDefinitionHandle(fieldRow++);
      var ctor = MetadataTokens.MethodDefinitionHandle(methodRow++);
      var invoke = MetadataTokens.MethodDefinitionHandle(methodRow++);
      ctx.BoundMethodClasses[bound] = new(td, recvField, ctor, invoke);
    }

    foreach (var seqElem in plan.SequenceElementTypes) {
      ctx.SeqInterfaces[seqElem] = new(
        MetadataTokens.TypeDefinitionHandle(typeRow++),
        MetadataTokens.MethodDefinitionHandle(methodRow++));
    }

    foreach (var iterFn in plan.IteratorFunctions) {
      var td = MetadataTokens.TypeDefinitionHandle(typeRow++);
      var paramFields = new FieldDefinitionHandle[iterFn.Parameters.Count];
      for (var i = 0; i < paramFields.Length; i++)
        paramFields[i] = MetadataTokens.FieldDefinitionHandle(fieldRow++);
      FieldDefinitionHandle? consumedField = iterFn.ReturnType.Restartable ? null : MetadataTokens.FieldDefinitionHandle(fieldRow++);
      var ctor = MetadataTokens.MethodDefinitionHandle(methodRow++);
      var enumerate = MetadataTokens.MethodDefinitionHandle(methodRow++);
      ctx.IterClasses[iterFn] = new(td, paramFields, consumedField, ctor, enumerate);
    }

    foreach (var arrSeqElem in plan.ArraySequenceElementTypes) {
      ctx.ArrSeqClasses[arrSeqElem] = new(
        MetadataTokens.TypeDefinitionHandle(typeRow++),
        MetadataTokens.FieldDefinitionHandle(fieldRow++),
        MetadataTokens.MethodDefinitionHandle(methodRow++),
        MetadataTokens.MethodDefinitionHandle(methodRow++));
    }

    foreach (var sliceElem in plan.SliceElementTypes) {
      ctx.SliceStructs[sliceElem] = new(
        MetadataTokens.TypeDefinitionHandle(typeRow++),
        MetadataTokens.FieldDefinitionHandle(fieldRow++),
        MetadataTokens.FieldDefinitionHandle(fieldRow++),
        MetadataTokens.FieldDefinitionHandle(fieldRow++));
    }

    if (plan.NeedsPointerStruct) {
      ctx.PtrStruct = new(
        MetadataTokens.TypeDefinitionHandle(typeRow++),
        MetadataTokens.FieldDefinitionHandle(fieldRow++),
        MetadataTokens.FieldDefinitionHandle(fieldRow++),
        MetadataTokens.FieldDefinitionHandle(fieldRow++),
        MetadataTokens.FieldDefinitionHandle(fieldRow++));
    }

    // Module static class
    typeRow++;
    foreach (var constant in module.Constants)
      ctx.ConstantFields[constant] = MetadataTokens.FieldDefinitionHandle(fieldRow++);
    foreach (var global in module.Globals)
      ctx.GlobalFields[global] = MetadataTokens.FieldDefinitionHandle(fieldRow++);

    if (needsCctor)
      methodRow++;
    foreach (var fn in freeFunctions)
      ctx.FunctionHandles[fn.Name] = MetadataTokens.MethodDefinitionHandle(methodRow++);

    if (plan.NeedsPext32)
      ctx.Pext32Handle = MetadataTokens.MethodDefinitionHandle(methodRow++);
    if (plan.NeedsPext64)
      ctx.Pext64Handle = MetadataTokens.MethodDefinitionHandle(methodRow++);
    if (plan.NeedsPdep32)
      ctx.Pdep32Handle = MetadataTokens.MethodDefinitionHandle(methodRow++);
    if (plan.NeedsPdep64)
      ctx.Pdep64Handle = MetadataTokens.MethodDefinitionHandle(methodRow++);

    foreach (var t in plan.PointerLoadTypes)
      ctx.PtrLoadHandles[t] = MetadataTokens.MethodDefinitionHandle(methodRow++);
    foreach (var t in plan.PointerStoreTypes)
      ctx.PtrStoreHandles[t] = MetadataTokens.MethodDefinitionHandle(methodRow++);
    foreach (var recCls in plan.RecordEqualsClasses)
      ctx.RecordEqualsHandles[recCls] = MetadataTokens.MethodDefinitionHandle(methodRow++);

    foreach (var foreign in module.ForeignFunctions)
      ctx.ForeignHandles[foreign.Name] = MetadataTokens.MethodDefinitionHandle(methodRow++);
  }

  private static void EmitEnums(MetadataBuilder metadata, MsilMetadataContext ctx, List<IrEnumType> enums, ref int nextFieldRow, int nextMethodRow) {
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
  }

  private static void EmitStructs(MetadataBuilder metadata, MsilMetadataContext ctx, List<IrStructType> structs, ref int nextFieldRow, int nextMethodRow) {
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
        nextFieldRow++;
      }
    }
  }

  private static void EmitInterfaces(MetadataBuilder metadata, MsilMetadataContext ctx, List<IrInterfaceType> interfaces, int nextFieldRow, ref int nextMethodRow, ref int nextParamRow) {
    foreach (var iface in interfaces) {
      var ifaceHandle = metadata.AddTypeDefinition(
        TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract,
        default,
        metadata.GetOrAddString(SanitizeName(iface.Name)),
        default,
        MetadataTokens.FieldDefinitionHandle(nextFieldRow),
        MetadataTokens.MethodDefinitionHandle(nextMethodRow));

      foreach (var baseIface in iface.BaseInterfaces)
        metadata.AddInterfaceImplementation(ifaceHandle, ctx.GetInterfaceToken(baseIface));

      foreach (var method in iface.Methods) {
        var fn = ctx.FunctionsByName[method.Function];
        var paramTypes = fn.Parameters.Skip(1).Select(p => p.Type).ToArray();
        metadata.AddMethodDefinition(
          MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Abstract,
          MethodImplAttributes.IL,
          metadata.GetOrAddString(ClrMethodName(method)),
          ctx.EncodeMethodSignature(fn.ReturnType, paramTypes, isInstance: true),
          bodyOffset: -1,
          MetadataTokens.ParameterHandle(nextParamRow));
        nextMethodRow++;

        for (var i = 1; i < fn.Parameters.Count; i++) {
          metadata.AddParameter(ParameterAttributes.None, metadata.GetOrAddString(fn.Parameters[i].Name), i);
          nextParamRow++;
        }
      }
    }
  }

  private static void EmitClasses(
    MetadataBuilder metadata,
    MsilMetadataContext ctx,
    List<IrClassType> classes,
    MethodBodyStreamEncoder methodBodies,
    ref int nextFieldRow,
    ref int nextMethodRow,
    ref int nextParamRow) {
    var systemObject = ctx.GetBclType("System", "Object");
    foreach (var cls in classes) {
      var attrs = TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.BeforeFieldInit;
      if (cls.IsAbstract)
        attrs |= TypeAttributes.Abstract;
      if (cls.IsSealed)
        attrs |= TypeAttributes.Sealed;

      var baseToken = cls.Base is { } baseCls ? ctx.GetClassToken(baseCls) : systemObject;
      var clsHandle = metadata.AddTypeDefinition(
        attrs,
        default,
        metadata.GetOrAddString(SanitizeName(cls.Name)),
        baseToken,
        MetadataTokens.FieldDefinitionHandle(nextFieldRow),
        MetadataTokens.MethodDefinitionHandle(nextMethodRow));

      foreach (var iface in cls.Interfaces)
        metadata.AddInterfaceImplementation(clsHandle, ctx.GetInterfaceToken(iface));

      foreach (var field in cls.Fields) {
        metadata.AddFieldDefinition(
          FieldAttributes.Public,
          metadata.GetOrAddString(field.Name),
          ctx.EncodeFieldSignature(field.Type));
        nextFieldRow++;
      }

      foreach (var ctor in cls.Constructors) {
        var fn = ctx.FunctionsByName[ctor.Function];
        var bodyOffset = MsilMethodCompiler.CompileClassConstructor(cls, ctor, fn, ctx, methodBodies);
        var paramTypes = fn.Parameters.Skip(1).Select(p => p.Type).ToArray();
        metadata.AddMethodDefinition(
          MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
          MethodImplAttributes.IL,
          metadata.GetOrAddString(".ctor"),
          ctx.EncodeMethodSignature(IrType.Void, paramTypes, isInstance: true),
          bodyOffset,
          MetadataTokens.ParameterHandle(nextParamRow));
        nextMethodRow++;

        for (var i = 1; i < fn.Parameters.Count; i++) {
          metadata.AddParameter(ParameterAttributes.None, metadata.GetOrAddString(fn.Parameters[i].Name), i);
          nextParamRow++;
        }
      }

      foreach (var method in cls.Methods) {
        var fn = ctx.FunctionsByName[method.Function];
        var isStatic = method.IsStatic;
        var paramTypes = (isStatic ? fn.Parameters : fn.Parameters.Skip(1)).Select(p => p.Type).ToArray();
        var methodAttrs = MapClassMethodAttributes(method);
        var bodyOffset = method.Dispatch == IrDispatch.Abstract
          ? -1
          : MsilMethodCompiler.CompileFunction(fn, ctx, methodBodies);

        var methodHandle = metadata.AddMethodDefinition(
          methodAttrs,
          MethodImplAttributes.IL,
          metadata.GetOrAddString(ClrMethodName(method)),
          ctx.EncodeMethodSignature(fn.ReturnType, paramTypes, isInstance: !isStatic),
          bodyOffset,
          MetadataTokens.ParameterHandle(nextParamRow));
        nextMethodRow++;

        var startIdx = isStatic ? 0 : 1;
        for (var i = startIdx; i < fn.Parameters.Count; i++) {
          metadata.AddParameter(ParameterAttributes.None, metadata.GetOrAddString(fn.Parameters[i].Name), i - startIdx + 1);
          nextParamRow++;
        }

        if (method.Dispatch != IrDispatch.Abstract) {
          var clrName = ClrMethodName(method);
          foreach (var ifaceMethod in method.Implements) {
            if (ClrMethodName(ifaceMethod) != clrName)
              metadata.AddMethodImplementation(clsHandle, methodHandle, ctx.GetMethodToken(ifaceMethod));
          }
        }
      }
    }
  }

  private static void EmitSynthesizedTypes(
    MetadataBuilder metadata,
    MsilSynthesisPlan plan,
    MsilMetadataContext ctx,
    MethodBodyStreamEncoder methodBodies,
    ref int nextFieldRow,
    ref int nextMethodRow,
    ref int nextParamRow) {
    var systemObject = ctx.GetBclType("System", "Object");
    var systemValueType = ctx.GetBclType("System", "ValueType");
    var systemArray = ctx.GetBclType("System", "Array");

    // 1. Closure capture cells ($Cell_N)
    for (var i = 0; i < plan.CellTypes.Count; i++) {
      var cellType = plan.CellTypes[i];
      metadata.AddTypeDefinition(
        TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
        default,
        metadata.GetOrAddString($"$Cell_{i}"),
        systemObject,
        MetadataTokens.FieldDefinitionHandle(nextFieldRow),
        MetadataTokens.MethodDefinitionHandle(nextMethodRow));

      metadata.AddFieldDefinition(FieldAttributes.Public, metadata.GetOrAddString("Value"), ctx.EncodeFieldSignature(cellType));
      nextFieldRow++;

      var ctorOffset = MsilMethodCompiler.CompileDefaultObjectCtor(ctx, methodBodies);
      metadata.AddMethodDefinition(
        MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
        MethodImplAttributes.IL,
        metadata.GetOrAddString(".ctor"),
        ctx.EncodeMethodSignature(IrType.Void, [], isInstance: true),
        ctorOffset,
        MetadataTokens.ParameterHandle(nextParamRow));
      nextMethodRow++;
    }

    // 2. Function invoker interfaces ($Fn_N)
    for (var i = 0; i < plan.FunctionTypes.Count; i++) {
      var fnType = plan.FunctionTypes[i];
      metadata.AddTypeDefinition(
        TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract,
        default,
        metadata.GetOrAddString($"$Fn_{i}"),
        default,
        MetadataTokens.FieldDefinitionHandle(nextFieldRow),
        MetadataTokens.MethodDefinitionHandle(nextMethodRow));

      metadata.AddMethodDefinition(
        MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Abstract,
        MethodImplAttributes.IL,
        metadata.GetOrAddString("Invoke"),
        ctx.EncodeMethodSignature(fnType.Result, fnType.Parameters, isInstance: true),
        bodyOffset: -1,
        MetadataTokens.ParameterHandle(nextParamRow));
      nextMethodRow++;

      for (var p = 0; p < fnType.Parameters.Count; p++) {
        metadata.AddParameter(ParameterAttributes.None, metadata.GetOrAddString($"p{p}"), p + 1);
        nextParamRow++;
      }
    }

    // 3. Lambda closure display classes ($Lambda_N)
    for (var i = 0; i < plan.Lambdas.Count; i++) {
      var lambda = plan.Lambdas[i];
      var handles = ctx.LambdaClasses[lambda];
      var sig = lambda.Type.Signature!;
      var td = metadata.AddTypeDefinition(
        TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
        default,
        metadata.GetOrAddString($"$Lambda_{i}"),
        systemObject,
        MetadataTokens.FieldDefinitionHandle(nextFieldRow),
        MetadataTokens.MethodDefinitionHandle(nextMethodRow));
      metadata.AddInterfaceImplementation(td, ctx.FnInterfaces[sig].Type);

      for (var c = 0; c < lambda.Captures.Count; c++) {
        var capCellTd = ctx.CellTypes[lambda.Captures[c].Type].Type;
        metadata.AddFieldDefinition(
          FieldAttributes.Public,
          metadata.GetOrAddString($"$c{c}"),
          ctx.EncodeCustomFieldSignature(enc => enc.Type(capCellTd, isValueType: false)));
        nextFieldRow++;
      }

      var ctorOffset = MsilMethodCompiler.CompileFieldStoringCtor(handles.CaptureFields, ctx, methodBodies);
      var ctorSig = ctx.EncodeCustomMethodSignature(
        isInstance: true,
        r => r.Void(),
        lambda.Captures.Count,
        p => {
          foreach (var cap in lambda.Captures)
            p.AddParameter().Type().Type(ctx.CellTypes[cap.Type].Type, isValueType: false);
        });
      metadata.AddMethodDefinition(
        MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
        MethodImplAttributes.IL,
        metadata.GetOrAddString(".ctor"),
        ctorSig,
        ctorOffset,
        MetadataTokens.ParameterHandle(nextParamRow));
      nextMethodRow++;
      for (var c = 0; c < lambda.Captures.Count; c++) {
        metadata.AddParameter(ParameterAttributes.None, metadata.GetOrAddString($"c{c}"), c + 1);
        nextParamRow++;
      }

      var invokeOffset = MsilMethodCompiler.CompileLambdaInvoke(lambda, handles.CaptureFields, ctx, methodBodies);
      metadata.AddMethodDefinition(
        MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Final,
        MethodImplAttributes.IL,
        metadata.GetOrAddString("Invoke"),
        ctx.EncodeMethodSignature(sig.Result, sig.Parameters, isInstance: true),
        invokeOffset,
        MetadataTokens.ParameterHandle(nextParamRow));
      nextMethodRow++;
      for (var p = 0; p < lambda.Parameters.Count; p++) {
        metadata.AddParameter(ParameterAttributes.None, metadata.GetOrAddString(lambda.Parameters[p].Name), p + 1);
        nextParamRow++;
      }
    }

    // 4. Function-reference adapter classes ($FnRef_N)
    for (var i = 0; i < plan.FunctionRefs.Count; i++) {
      var (fnName, sig) = plan.FunctionRefs[i];
      var td = metadata.AddTypeDefinition(
        TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
        default,
        metadata.GetOrAddString($"$FnRef_{i}"),
        systemObject,
        MetadataTokens.FieldDefinitionHandle(nextFieldRow),
        MetadataTokens.MethodDefinitionHandle(nextMethodRow));
      metadata.AddInterfaceImplementation(td, ctx.FnInterfaces[sig].Type);

      var ctorOffset = MsilMethodCompiler.CompileDefaultObjectCtor(ctx, methodBodies);
      metadata.AddMethodDefinition(
        MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
        MethodImplAttributes.IL,
        metadata.GetOrAddString(".ctor"),
        ctx.EncodeMethodSignature(IrType.Void, [], isInstance: true),
        ctorOffset,
        MetadataTokens.ParameterHandle(nextParamRow));
      nextMethodRow++;

      var invokeOffset = MsilMethodCompiler.CompileFunctionRefInvoke(fnName, sig, ctx, methodBodies);
      metadata.AddMethodDefinition(
        MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Final,
        MethodImplAttributes.IL,
        metadata.GetOrAddString("Invoke"),
        ctx.EncodeMethodSignature(sig.Result, sig.Parameters, isInstance: true),
        invokeOffset,
        MetadataTokens.ParameterHandle(nextParamRow));
      nextMethodRow++;
      for (var p = 0; p < sig.Parameters.Count; p++) {
        metadata.AddParameter(ParameterAttributes.None, metadata.GetOrAddString($"p{p}"), p + 1);
        nextParamRow++;
      }
    }

    // 5. Bound-method adapter classes ($Bound_N)
    for (var i = 0; i < plan.BoundMethods.Count; i++) {
      var key = plan.BoundMethods[i];
      var handles = ctx.BoundMethodClasses[key];
      var sig = key.Signature;
      var fn = ctx.FunctionsByName[key.Method.Function];
      var td = metadata.AddTypeDefinition(
        TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
        default,
        metadata.GetOrAddString($"$Bound_{i}"),
        systemObject,
        MetadataTokens.FieldDefinitionHandle(nextFieldRow),
        MetadataTokens.MethodDefinitionHandle(nextMethodRow));
      metadata.AddInterfaceImplementation(td, ctx.FnInterfaces[sig].Type);

      IrType? receiverType = key.Method.IsStatic ? null : fn.Parameters[0].Type;
      if (receiverType is not null) {
        metadata.AddFieldDefinition(FieldAttributes.Public, metadata.GetOrAddString("_receiver"), ctx.EncodeFieldSignature(receiverType));
        nextFieldRow++;
      }

      var ctorFields = handles.ReceiverField is { } rf ? new[] { rf } : Array.Empty<FieldDefinitionHandle>();
      var ctorOffset = MsilMethodCompiler.CompileFieldStoringCtor(ctorFields, ctx, methodBodies);
      var ctorParamTypes = receiverType is not null ? new[] { receiverType } : Array.Empty<IrType>();
      metadata.AddMethodDefinition(
        MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
        MethodImplAttributes.IL,
        metadata.GetOrAddString(".ctor"),
        ctx.EncodeMethodSignature(IrType.Void, ctorParamTypes, isInstance: true),
        ctorOffset,
        MetadataTokens.ParameterHandle(nextParamRow));
      nextMethodRow++;
      if (receiverType is not null) {
        metadata.AddParameter(ParameterAttributes.None, metadata.GetOrAddString("receiver"), 1);
        nextParamRow++;
      }

      var invokeOffset = MsilMethodCompiler.CompileBoundMethodInvoke(key, handles.ReceiverField, ctx, methodBodies);
      metadata.AddMethodDefinition(
        MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Final,
        MethodImplAttributes.IL,
        metadata.GetOrAddString("Invoke"),
        ctx.EncodeMethodSignature(sig.Result, sig.Parameters, isInstance: true),
        invokeOffset,
        MetadataTokens.ParameterHandle(nextParamRow));
      nextMethodRow++;
      for (var p = 0; p < sig.Parameters.Count; p++) {
        metadata.AddParameter(ParameterAttributes.None, metadata.GetOrAddString($"p{p}"), p + 1);
        nextParamRow++;
      }
    }

    // 6. Sequence interfaces ($Seq_N)
    for (var i = 0; i < plan.SequenceElementTypes.Count; i++) {
      var elem = plan.SequenceElementTypes[i];
      metadata.AddTypeDefinition(
        TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract,
        default,
        metadata.GetOrAddString($"$Seq_{i}"),
        default,
        MetadataTokens.FieldDefinitionHandle(nextFieldRow),
        MetadataTokens.MethodDefinitionHandle(nextMethodRow));

      metadata.AddMethodDefinition(
        MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Abstract,
        MethodImplAttributes.IL,
        metadata.GetOrAddString("Enumerate"),
        ctx.EncodeMethodSignature(IrType.List(elem), [], isInstance: true),
        bodyOffset: -1,
        MetadataTokens.ParameterHandle(nextParamRow));
      nextMethodRow++;
    }

    // 7. Iterator sequence classes ($Iter_N)
    for (var i = 0; i < plan.IteratorFunctions.Count; i++) {
      var fn = plan.IteratorFunctions[i];
      var elem = fn.ReturnType.Element!;
      var handles = ctx.IterClasses[fn];
      var td = metadata.AddTypeDefinition(
        TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
        default,
        metadata.GetOrAddString($"$Iter_{i}"),
        systemObject,
        MetadataTokens.FieldDefinitionHandle(nextFieldRow),
        MetadataTokens.MethodDefinitionHandle(nextMethodRow));
      metadata.AddInterfaceImplementation(td, ctx.SeqInterfaces[elem].Type);

      for (var p = 0; p < fn.Parameters.Count; p++) {
        metadata.AddFieldDefinition(FieldAttributes.Public, metadata.GetOrAddString($"$p{p}"), ctx.EncodeFieldSignature(fn.Parameters[p].Type));
        nextFieldRow++;
      }
      if (handles.ConsumedField is not null) {
        metadata.AddFieldDefinition(FieldAttributes.Public, metadata.GetOrAddString("$consumed"), ctx.EncodeFieldSignature(IrType.Bool));
        nextFieldRow++;
      }

      var ctorOffset = MsilMethodCompiler.CompileFieldStoringCtor(handles.ParamFields, ctx, methodBodies);
      var paramTypes = fn.Parameters.Select(p => p.Type).ToArray();
      metadata.AddMethodDefinition(
        MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
        MethodImplAttributes.IL,
        metadata.GetOrAddString(".ctor"),
        ctx.EncodeMethodSignature(IrType.Void, paramTypes, isInstance: true),
        ctorOffset,
        MetadataTokens.ParameterHandle(nextParamRow));
      nextMethodRow++;
      for (var p = 0; p < fn.Parameters.Count; p++) {
        metadata.AddParameter(ParameterAttributes.None, metadata.GetOrAddString(fn.Parameters[p].Name), p + 1);
        nextParamRow++;
      }

      var enumOffset = MsilMethodCompiler.CompileIteratorEnumerate(fn, handles, ctx, methodBodies);
      metadata.AddMethodDefinition(
        MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Final,
        MethodImplAttributes.IL,
        metadata.GetOrAddString("Enumerate"),
        ctx.EncodeMethodSignature(IrType.List(elem), [], isInstance: true),
        enumOffset,
        MetadataTokens.ParameterHandle(nextParamRow));
      nextMethodRow++;
    }

    // 8. Array sequence adapter classes ($ArrSeq_N)
    for (var i = 0; i < plan.ArraySequenceElementTypes.Count; i++) {
      var elem = plan.ArraySequenceElementTypes[i];
      var handles = ctx.ArrSeqClasses[elem];
      var td = metadata.AddTypeDefinition(
        TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
        default,
        metadata.GetOrAddString($"$ArrSeq_{i}"),
        systemObject,
        MetadataTokens.FieldDefinitionHandle(nextFieldRow),
        MetadataTokens.MethodDefinitionHandle(nextMethodRow));
      metadata.AddInterfaceImplementation(td, ctx.SeqInterfaces[elem].Type);

      metadata.AddFieldDefinition(FieldAttributes.Public, metadata.GetOrAddString("_array"), ctx.EncodeFieldSignature(IrType.Array(elem)));
      nextFieldRow++;

      var ctorOffset = MsilMethodCompiler.CompileFieldStoringCtor([handles.ArrayField], ctx, methodBodies);
      metadata.AddMethodDefinition(
        MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
        MethodImplAttributes.IL,
        metadata.GetOrAddString(".ctor"),
        ctx.EncodeMethodSignature(IrType.Void, [IrType.Array(elem)], isInstance: true),
        ctorOffset,
        MetadataTokens.ParameterHandle(nextParamRow));
      nextMethodRow++;
      metadata.AddParameter(ParameterAttributes.None, metadata.GetOrAddString("array"), 1);
      nextParamRow++;

      var enumOffset = MsilMethodCompiler.CompileArraySeqEnumerate(elem, handles.ArrayField, ctx, methodBodies);
      metadata.AddMethodDefinition(
        MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Final,
        MethodImplAttributes.IL,
        metadata.GetOrAddString("Enumerate"),
        ctx.EncodeMethodSignature(IrType.List(elem), [], isInstance: true),
        enumOffset,
        MetadataTokens.ParameterHandle(nextParamRow));
      nextMethodRow++;
    }

    // 9. Value-type slice structs ($Slice_N)
    for (var i = 0; i < plan.SliceElementTypes.Count; i++) {
      var elem = plan.SliceElementTypes[i];
      metadata.AddTypeDefinition(
        TypeAttributes.Public | TypeAttributes.SequentialLayout | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
        default,
        metadata.GetOrAddString($"$Slice_{i}"),
        systemValueType,
        MetadataTokens.FieldDefinitionHandle(nextFieldRow),
        MetadataTokens.MethodDefinitionHandle(nextMethodRow));

      metadata.AddFieldDefinition(FieldAttributes.Public, metadata.GetOrAddString("Array"), ctx.EncodeFieldSignature(IrType.Array(elem)));
      metadata.AddFieldDefinition(FieldAttributes.Public, metadata.GetOrAddString("Offset"), ctx.EncodeFieldSignature(IrType.I32));
      metadata.AddFieldDefinition(FieldAttributes.Public, metadata.GetOrAddString("Length"), ctx.EncodeFieldSignature(IrType.I32));
      nextFieldRow += 3;
    }

    // 10. Value-type bounded pointer struct ($Ptr)
    if (plan.NeedsPointerStruct) {
      metadata.AddTypeDefinition(
        TypeAttributes.Public | TypeAttributes.SequentialLayout | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
        default,
        metadata.GetOrAddString("$Ptr"),
        systemValueType,
        MetadataTokens.FieldDefinitionHandle(nextFieldRow),
        MetadataTokens.MethodDefinitionHandle(nextMethodRow));

      metadata.AddFieldDefinition(
        FieldAttributes.Public,
        metadata.GetOrAddString("Backing"),
        ctx.EncodeCustomFieldSignature(enc => enc.Type(systemArray, isValueType: false)));
      metadata.AddFieldDefinition(FieldAttributes.Public, metadata.GetOrAddString("ByteOffset"), ctx.EncodeFieldSignature(IrType.I32));
      metadata.AddFieldDefinition(FieldAttributes.Public, metadata.GetOrAddString("RegionStart"), ctx.EncodeFieldSignature(IrType.I32));
      metadata.AddFieldDefinition(FieldAttributes.Public, metadata.GetOrAddString("RegionBytes"), ctx.EncodeFieldSignature(IrType.I32));
      nextFieldRow += 4;
    }
  }

  private static MethodDefinitionHandle EmitModuleClass(
    MetadataBuilder metadata,
    IrModule module,
    MsilSynthesisPlan plan,
    MsilMetadataContext ctx,
    string className,
    List<IrFunction> freeFunctions,
    bool needsCctor,
    bool isExe,
    MethodBodyStreamEncoder methodBodies,
    ref int nextFieldRow,
    ref int nextMethodRow,
    ref int nextParamRow) {
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

    foreach (var constant in module.Constants) {
      metadata.AddFieldDefinition(
        FieldAttributes.Assembly | FieldAttributes.Static | FieldAttributes.InitOnly,
        metadata.GetOrAddString($"$const_{constant.Name}"),
        ctx.EncodeFieldSignature(constant.Type));
      nextFieldRow++;
    }

    foreach (var global in module.Globals) {
      metadata.AddFieldDefinition(
        FieldAttributes.Public | FieldAttributes.Static,
        metadata.GetOrAddString(global.Name),
        ctx.EncodeFieldSignature(global.Type));
      nextFieldRow++;
    }

    if (needsCctor) {
      var cctorOffset = MsilMethodCompiler.CompileStaticCtor(module, ctx, methodBodies);
      metadata.AddMethodDefinition(
        MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
        MethodImplAttributes.IL,
        metadata.GetOrAddString(".cctor"),
        ctx.EncodeMethodSignature(IrType.Void, []),
        cctorOffset,
        MetadataTokens.ParameterHandle(nextParamRow));
      nextMethodRow++;
    }

    foreach (var fn in freeFunctions) {
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
      nextMethodRow++;

      for (var i = 0; i < fn.Parameters.Count; i++) {
        metadata.AddParameter(ParameterAttributes.None, metadata.GetOrAddString(fn.Parameters[i].Name), i + 1);
        nextParamRow++;
      }
    }

    // Emit bit-permutation, pointer load/store, and record equality helpers.
    if (plan.NeedsPext32) {
      var offset = MsilMethodCompiler.CompileBitPermutationHelper(isExtract: true, is64: false, ctx, methodBodies);
      metadata.AddMethodDefinition(
        MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig,
        MethodImplAttributes.IL,
        metadata.GetOrAddString("$Pext32"),
        ctx.EncodeMethodSignature(IrType.U32, [IrType.U32, IrType.U32, IrType.I32]),
        offset,
        MetadataTokens.ParameterHandle(nextParamRow));
      nextMethodRow++;
    }

    if (plan.NeedsPext64) {
      var offset = MsilMethodCompiler.CompileBitPermutationHelper(isExtract: true, is64: true, ctx, methodBodies);
      metadata.AddMethodDefinition(
        MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig,
        MethodImplAttributes.IL,
        metadata.GetOrAddString("$Pext64"),
        ctx.EncodeMethodSignature(IrType.U64, [IrType.U64, IrType.U64]),
        offset,
        MetadataTokens.ParameterHandle(nextParamRow));
      nextMethodRow++;
    }

    if (plan.NeedsPdep32) {
      var offset = MsilMethodCompiler.CompileBitPermutationHelper(isExtract: false, is64: false, ctx, methodBodies);
      metadata.AddMethodDefinition(
        MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig,
        MethodImplAttributes.IL,
        metadata.GetOrAddString("$Pdep32"),
        ctx.EncodeMethodSignature(IrType.U32, [IrType.U32, IrType.U32, IrType.I32]),
        offset,
        MetadataTokens.ParameterHandle(nextParamRow));
      nextMethodRow++;
    }

    if (plan.NeedsPdep64) {
      var offset = MsilMethodCompiler.CompileBitPermutationHelper(isExtract: false, is64: true, ctx, methodBodies);
      metadata.AddMethodDefinition(
        MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig,
        MethodImplAttributes.IL,
        metadata.GetOrAddString("$Pdep64"),
        ctx.EncodeMethodSignature(IrType.U64, [IrType.U64, IrType.U64]),
        offset,
        MetadataTokens.ParameterHandle(nextParamRow));
      nextMethodRow++;
    }

    for (var i = 0; i < plan.PointerLoadTypes.Count; i++) {
      var t = plan.PointerLoadTypes[i];
      var offset = MsilMethodCompiler.CompilePointerLoadHelper(t, ctx, methodBodies);
      metadata.AddMethodDefinition(
        MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig,
        MethodImplAttributes.IL,
        metadata.GetOrAddString($"$PtrLoad_{i}"),
        ctx.EncodeMethodSignature(t, [IrType.Pointer(t)]),
        offset,
        MetadataTokens.ParameterHandle(nextParamRow));
      nextMethodRow++;
    }

    for (var i = 0; i < plan.PointerStoreTypes.Count; i++) {
      var t = plan.PointerStoreTypes[i];
      var offset = MsilMethodCompiler.CompilePointerStoreHelper(t, ctx, methodBodies);
      metadata.AddMethodDefinition(
        MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig,
        MethodImplAttributes.IL,
        metadata.GetOrAddString($"$PtrStore_{i}"),
        ctx.EncodeMethodSignature(IrType.Void, [IrType.Pointer(t), t]),
        offset,
        MetadataTokens.ParameterHandle(nextParamRow));
      nextMethodRow++;
    }

    for (var i = 0; i < plan.RecordEqualsClasses.Count; i++) {
      var recCls = plan.RecordEqualsClasses[i];
      var recType = IrType.Object(recCls);
      var offset = MsilMethodCompiler.CompileRecordEqualsHelper(recCls, ctx, methodBodies);
      metadata.AddMethodDefinition(
        MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig,
        MethodImplAttributes.IL,
        metadata.GetOrAddString($"$RecEq_{i}"),
        ctx.EncodeMethodSignature(IrType.Bool, [recType, recType]),
        offset,
        MetadataTokens.ParameterHandle(nextParamRow));
      nextMethodRow++;
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
      nextMethodRow++;

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
      var entryCandidate = FindEntryPointCandidate(freeFunctions);
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

    return entryPoint;
  }

  private static MethodAttributes MapClassMethodAttributes(IrMethod method) {
    if (method.IsStatic)
      return MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig;

    return method.Dispatch switch {
      IrDispatch.Abstract => MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Abstract,
      IrDispatch.Virtual => MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
      IrDispatch.Override => MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig
        | (method.IsSealedOverride ? MethodAttributes.Final : default),
      _ => method.Implements.Count > 0
        ? MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Final
        : MethodAttributes.Public | MethodAttributes.HideBySig
    };
  }

  private static string ClrMethodName(IrMethod method) => method.Kind switch {
    IrMethodKind.Getter => method.Name.StartsWith("get_", StringComparison.Ordinal) ? method.Name : "get_" + method.Name,
    IrMethodKind.Setter => method.Name.StartsWith("set_", StringComparison.Ordinal) ? method.Name : "set_" + method.Name,
    _ => method.Name
  };

  /// <summary>Selects an entry-point candidate function (<c>Main</c> or <c>main</c>) if present.</summary>
  private static IrFunction? FindEntryPointCandidate(IReadOnlyList<IrFunction> functions) {
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

    return functions.FirstOrDefault(fn => fn.Name == "Main" && IsEligible(fn))
      ?? functions.FirstOrDefault(fn => string.Equals(fn.Name, "main", StringComparison.OrdinalIgnoreCase) && IsEligible(fn));
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
