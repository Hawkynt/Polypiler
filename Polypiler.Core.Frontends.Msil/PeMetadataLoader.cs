using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Msil;

/// <summary>Information about a static method discovered in the loaded PE assembly.</summary>
internal sealed record MethodEntry(
  MethodDefinitionHandle Handle,
  TypeDefinitionHandle DeclaringType,
  string Namespace,
  string TypeName,
  string MethodName,
  string IrName,
  bool IsPublic);

/// <summary>Resolved target of a <c>call</c>, <c>callvirt</c>, or <c>newobj</c> metadata token.</summary>
internal sealed record ResolvedCall(
  bool IsIntraAssembly,
  MethodDefinitionHandle LocalHandle,
  string DeclaringType,
  string MethodName,
  bool HasThis,
  MsilType ReturnType,
  IReadOnlyList<MsilType> ParameterTypes);

/// <summary>A module-level static field mapped to an <see cref="IrGlobal"/>.</summary>
internal sealed class StaticFieldSlot(
  FieldDefinitionHandle handle,
  TypeDefinitionHandle declaringType,
  string name,
  IrType type) {

  public FieldDefinitionHandle Handle { get; } = handle;
  public TypeDefinitionHandle DeclaringType { get; } = declaringType;
  public string Name { get; } = name;
  public IrType Type { get; } = type;

}

/// <summary>
/// Reads PE32 / PE32+ managed assemblies via <see cref="PEReader"/> and <see cref="MetadataReader"/>,
/// resolves metadata tokens, discovers methods/static fields, and orchestrates lifting into <see cref="IrModule"/>.
/// </summary>
internal sealed class PeMetadataLoader : IDisposable {

  private readonly PEReader _peReader;
  private readonly MetadataReader _metadata;
  private readonly string _fileName;
  private readonly Dictionary<MethodDefinitionHandle, MethodEntry> _allMethods = [];
  private readonly Dictionary<TypeDefinitionHandle, MethodDefinitionHandle> _cctors = [];
  private readonly Dictionary<FieldDefinitionHandle, StaticFieldSlot> _staticFields = [];
  private readonly Queue<MethodEntry> _pending = new();
  private readonly HashSet<MethodDefinitionHandle> _queued = [];
  private readonly List<StaticFieldSlot> _usedFields = [];
  private readonly HashSet<FieldDefinitionHandle> _usedFieldSet = [];
  private readonly Dictionary<FieldDefinitionHandle, IrGlobal> _globalsByField = [];
  private readonly MsilSignatureTypeProvider _rootProvider = new();

  private PeMetadataLoader(string fileName, PEReader peReader, MetadataReader metadata) {
    this._fileName = fileName;
    this._peReader = peReader;
    this._metadata = metadata;
  }

  /// <summary>Opens and validates a PE/CLI managed assembly from raw bytes.</summary>
  public static PeMetadataLoader Load(string fileName, byte[] assemblyBytes) {
    if (assemblyBytes.Length < 64)
      throw new MsilImportException(MsilFrontend.NotPeOrCli, $"'{fileName}' is too small to be a valid PE/CLI assembly");

    PEReader peReader;
    try {
      peReader = new PEReader(ImmutableArray.Create(assemblyBytes));
      if (!peReader.HasMetadata || peReader.PEHeaders.CorHeader is null) {
        peReader.Dispose();
        throw new MsilImportException(MsilFrontend.NotPeOrCli, $"'{fileName}' does not contain a CLI header or metadata");
      }
    } catch (BadImageFormatException ex) {
      throw new MsilImportException(MsilFrontend.NotPeOrCli, $"'{fileName}' is not a valid PE image: {ex.Message}");
    }

    var corHeader = peReader.PEHeaders.CorHeader!;
    if ((corHeader.Flags & CorFlags.ILOnly) == 0) {
      peReader.Dispose();
      throw new MsilImportException(MsilFrontend.MixedModeUnsupported, $"'{fileName}' is a mixed-mode (non-ILOnly) assembly, which is not supported");
    }

    var metadata = peReader.GetMetadataReader();
    if (!metadata.IsAssembly) {
      peReader.Dispose();
      throw new MsilImportException(MsilFrontend.CorruptMetadata, $"'{fileName}' has metadata but is not a managed assembly");
    }

    return new(fileName, peReader, metadata);
  }

  /// <summary>The underlying metadata reader.</summary>
  public MetadataReader Metadata => this._metadata;

  /// <summary>Lifts the selected methods and referenced static fields into an <see cref="IrModule"/>.</summary>
  public IrModule LiftModule(MsilImportOptions? options) {
    this.IndexAssembly();
    var selected = this.SelectRoots(options);

    foreach (var entry in selected)
      this.Enqueue(entry);

    var orderedEntries = new List<MethodEntry>();
    var rawLifts = new Dictionary<MethodDefinitionHandle, MethodLifter.PreparedMethod>();
    while (this._pending.Count > 0) {
      var entry = this._pending.Dequeue();
      orderedEntries.Add(entry);
      rawLifts[entry.Handle] = MethodLifter.Prepare(this, entry);
    }

    var globals = this.BuildGlobals();
    var functions = new List<IrFunction>(orderedEntries.Count);
    foreach (var entry in orderedEntries)
      functions.Add(MethodLifter.Lift(this, rawLifts[entry.Handle]));

    var moduleName = SanitizeIdentifier(this._metadata.GetString(this._metadata.GetAssemblyDefinition().Name));
    return new IrModule(moduleName, functions, []) { Globals = globals };
  }

  /// <summary>Queues an intra-assembly static method for lifting and returns its IR entry.</summary>
  public MethodEntry RequireIntraAssemblyMethod(MethodDefinitionHandle handle) {
    var entry = this._allMethods[handle];
    this.ValidateMethodBody(entry);
    this.Enqueue(entry);
    return entry;
  }

  /// <summary>Marks a static field as used and returns its <see cref="StaticFieldSlot"/>.</summary>
  public StaticFieldSlot RequireStaticField(FieldDefinitionHandle handle) {
    if (!this._staticFields.TryGetValue(handle, out var slot)) {
      var def = this._metadata.GetFieldDefinition(handle);
      var name = this._metadata.GetString(def.Name);
      var sig = def.DecodeSignature(this._rootProvider, null);
      var irType = sig.RequireIr($"static field '{name}'");
      slot = new(handle, def.GetDeclaringType(), this.MakeGlobalName(def.GetDeclaringType(), name), irType);
      this._staticFields[handle] = slot;
    }

    if (this._usedFieldSet.Add(handle))
      this._usedFields.Add(slot);

    return slot;
  }

  /// <summary>Returns the materialized <see cref="IrGlobal"/> for a used static field.</summary>
  public IrGlobal GetGlobal(FieldDefinitionHandle handle) => this._globalsByField[handle];

  /// <summary>Reads the <see cref="MethodBodyBlock"/> of a method definition.</summary>
  public MethodBodyBlock GetMethodBody(MethodDefinitionHandle handle)
    => this._peReader.GetMethodBody(this._metadata.GetMethodDefinition(handle).RelativeVirtualAddress);

  /// <summary>Decodes the signature of a method definition.</summary>
  public MethodSignature<MsilType> DecodeMethodSignature(MethodDefinitionHandle handle)
    => this._metadata.GetMethodDefinition(handle).DecodeSignature(this._rootProvider, null);

  /// <summary>Decodes local variable types for a method body.</summary>
  public ImmutableArray<MsilType> DecodeLocals(StandaloneSignatureHandle handle)
    => handle.IsNil ? [] : this._metadata.GetStandaloneSignature(handle).DecodeLocalSignature(this._rootProvider, null);

  /// <summary>Resolves a type token (<c>TypeDef</c>, <c>TypeRef</c>, or <c>TypeSpec</c>).</summary>
  public MsilType ResolveTypeToken(int token) {
    var handle = MetadataTokens.EntityHandle(token);
    return handle.Kind switch {
      HandleKind.TypeDefinition => this._rootProvider.GetTypeFromDefinition(this._metadata, (TypeDefinitionHandle)handle, 0),
      HandleKind.TypeReference => this._rootProvider.GetTypeFromReference(this._metadata, (TypeReferenceHandle)handle, 0),
      HandleKind.TypeSpecification => this._rootProvider.GetTypeFromSpecification(this._metadata, null, (TypeSpecificationHandle)handle, 0),
      _ => throw new MsilImportException(MsilFrontend.CorruptMetadata, $"token 0x{token:X8} is not a type token")
    };
  }

  /// <summary>Resolves a field token to a local <see cref="FieldDefinitionHandle"/>.</summary>
  public FieldDefinitionHandle ResolveFieldToken(int token) {
    var handle = MetadataTokens.EntityHandle(token);
    if (handle.Kind == HandleKind.FieldDefinition)
      return (FieldDefinitionHandle)handle;

    throw new MsilImportException(MsilFrontend.UnsupportedCall, $"external field token 0x{token:X8} is not supported");
  }

  /// <summary>Resolves a user string token (<c>ldstr</c>).</summary>
  public string ResolveUserString(int token)
    => this._metadata.GetUserString(MetadataTokens.UserStringHandle(token));

  /// <summary>Resolves a <c>call</c>, <c>callvirt</c>, or <c>newobj</c> target token.</summary>
  public ResolvedCall ResolveCallToken(int token) {
    var handle = MetadataTokens.EntityHandle(token);
    return handle.Kind switch {
      HandleKind.MethodDefinition => this.ResolveMethodDef((MethodDefinitionHandle)handle),
      HandleKind.MemberReference => this.ResolveMemberRef((MemberReferenceHandle)handle, null),
      HandleKind.MethodSpecification => this.ResolveMethodSpec((MethodSpecificationHandle)handle),
      _ => throw new MsilImportException(MsilFrontend.CorruptMetadata, $"token 0x{token:X8} is not a method token")
    };
  }

  /// <summary>Reads constant array initializer elements from an RVA-backed field (<c>RuntimeHelpers.InitializeArray</c>).</summary>
  public IReadOnlyList<IrExpr> ReadRvaArrayElements(FieldDefinitionHandle fieldHandle, IrType elementType, int length) {
    var rva = this._metadata.GetFieldDefinition(fieldHandle).GetRelativeVirtualAddress();
    var elemSize = elementType.Size;
    var byteCount = checked(length * elemSize);
    var bytes = this._peReader.GetSectionData(rva).GetContent(0, byteCount);
    var span = bytes.AsSpan();
    var elements = new IrExpr[length];

    for (var i = 0; i < length; ++i) {
      var slice = span.Slice(i * elemSize, elemSize);
      elements[i] = elementType.Kind switch {
        IrTypeKind.I8 => new IrConst(IrType.I8, (sbyte)slice[0]),
        IrTypeKind.U8 => new IrConst(IrType.U8, slice[0]),
        IrTypeKind.I16 => new IrConst(IrType.I16, BinaryPrimitives.ReadInt16LittleEndian(slice)),
        IrTypeKind.U16 => new IrConst(IrType.U16, BinaryPrimitives.ReadUInt16LittleEndian(slice)),
        IrTypeKind.I32 => new IrConst(IrType.I32, BinaryPrimitives.ReadInt32LittleEndian(slice)),
        IrTypeKind.U32 => new IrConst(IrType.U32, BinaryPrimitives.ReadUInt32LittleEndian(slice)),
        IrTypeKind.I64 => new IrConst(IrType.I64, BinaryPrimitives.ReadInt64LittleEndian(slice)),
        IrTypeKind.U64 => new IrConst(IrType.U64, unchecked((long)BinaryPrimitives.ReadUInt64LittleEndian(slice))),
        IrTypeKind.F32 => new IrConst(IrType.F32, BinaryPrimitives.ReadInt32LittleEndian(slice)),
        _ => new IrConst(IrType.F64, BinaryPrimitives.ReadInt64LittleEndian(slice))
      };
    }

    return elements;
  }

  /// <summary>Gets the CLR parameter names for a method definition, falling back to <c>p0</c>, <c>p1</c>, ...</summary>
  public IReadOnlyList<string> GetParameterNames(MethodDefinitionHandle handle, int count) {
    var names = new string[count];
    foreach (var paramHandle in this._metadata.GetMethodDefinition(handle).GetParameters()) {
      var param = this._metadata.GetParameter(paramHandle);
      var index = param.SequenceNumber - 1;
      if (index >= 0 && index < count)
        names[index] = SanitizeIdentifier(this._metadata.GetString(param.Name));
    }

    for (var i = 0; i < count; ++i)
      if (string.IsNullOrEmpty(names[i]))
        names[i] = $"p{i}";

    return names;
  }

  public void Dispose() => this._peReader.Dispose();

  private void IndexAssembly() {
    var rawMethods = new List<(MethodDefinitionHandle Handle, TypeDefinitionHandle TypeHandle, string Ns, string TypeName, string MethodName, bool IsPublic)>();

    foreach (var typeHandle in this._metadata.TypeDefinitions) {
      var typeDef = this._metadata.GetTypeDefinition(typeHandle);
      var typeName = this._metadata.GetString(typeDef.Name);
      if (typeName.StartsWith('<'))
        continue;

      var ns = this._metadata.GetString(typeDef.Namespace);
      var typePublic = (typeDef.Attributes & TypeAttributes.VisibilityMask) is TypeAttributes.Public or TypeAttributes.NestedPublic;

      foreach (var methodHandle in typeDef.GetMethods()) {
        var methodDef = this._metadata.GetMethodDefinition(methodHandle);
        if ((methodDef.Attributes & MethodAttributes.Static) == 0)
          continue;

        var methodName = this._metadata.GetString(methodDef.Name);
        if (methodName == ".cctor") {
          this._cctors[typeHandle] = methodHandle;
          continue;
        }

        if (methodName.StartsWith('<'))
          continue;

        var isPublic = typePublic && (methodDef.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public;
        rawMethods.Add((methodHandle, typeHandle, ns, typeName, methodName, isPublic));
      }
    }

    var nameCounts = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var m in rawMethods)
      nameCounts[m.MethodName] = nameCounts.GetValueOrDefault(m.MethodName) + 1;

    var usedIrNames = new HashSet<string>(StringComparer.Ordinal);
    foreach (var m in rawMethods) {
      var baseName = nameCounts[m.MethodName] == 1
        ? SanitizeIdentifier(m.MethodName)
        : SanitizeIdentifier($"{m.TypeName}_{m.MethodName}");

      var irName = baseName;
      var suffix = 1;
      while (!usedIrNames.Add(irName))
        irName = $"{baseName}_{suffix++}";

      this._allMethods[m.Handle] = new(m.Handle, m.TypeHandle, m.Ns, m.TypeName, m.MethodName, irName, m.IsPublic);
    }
  }

  private List<MethodEntry> SelectRoots(MsilImportOptions? options) {
    var exports = options?.Exports;
    if (exports is { Count: > 0 }) {
      var selected = new List<MethodEntry>();
      var seen = new HashSet<MethodDefinitionHandle>();

      foreach (var selector in exports) {
        var matches = this._allMethods.Values.Where(m => MatchesSelector(m, selector)).ToList();
        if (matches.Count == 0)
          throw new MsilImportException(MsilFrontend.ExportNotFound, $"export '{selector}' was not found in '{this._fileName}'");

        if (matches.Count > 1)
          throw new MsilImportException(MsilFrontend.ExportNotFound, $"export '{selector}' is ambiguous in '{this._fileName}' ({matches.Count} matches)");

        var entry = matches[0];
        this.ValidateMethodBody(entry);
        if (seen.Add(entry.Handle))
          selected.Add(entry with { IsPublic = true });
      }

      return selected;
    }

    var all = new List<MethodEntry>();
    foreach (var entry in this._allMethods.Values) {
      this.ValidateMethodBody(entry);
      all.Add(entry);
    }

    return all;
  }

  private void ValidateMethodBody(MethodEntry entry) {
    var def = this._metadata.GetMethodDefinition(entry.Handle);
    var hasBody = (def.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) == 0
      && (def.ImplAttributes & MethodImplAttributes.CodeTypeMask) == MethodImplAttributes.IL
      && def.RelativeVirtualAddress != 0;

    if (!hasBody)
      throw new MsilImportException(MsilFrontend.UnsupportedMethodBody, $"method '{entry.TypeName}.{entry.MethodName}' has no supported IL body");

    if (def.GetGenericParameters().Count > 0)
      throw new MsilImportException(MsilFrontend.UnsupportedType, $"generic method '{entry.TypeName}.{entry.MethodName}' is not supported");
  }

  private static bool MatchesSelector(MethodEntry entry, string selector) {
    if (string.Equals(entry.MethodName, selector, StringComparison.Ordinal))
      return true;

    var typeAndMethod = $"{entry.TypeName}.{entry.MethodName}";
    return string.Equals(typeAndMethod, selector, StringComparison.Ordinal)
      || (!string.IsNullOrEmpty(entry.Namespace) && string.Equals($"{entry.Namespace}.{typeAndMethod}", selector, StringComparison.Ordinal));
  }

  private void Enqueue(MethodEntry entry) {
    if (this._queued.Add(entry.Handle))
      this._pending.Enqueue(entry);
  }

  private List<IrGlobal> BuildGlobals() {
    if (this._usedFields.Count == 0)
      return [];

    var initializers = new Dictionary<FieldDefinitionHandle, IrExpr>();
    foreach (var owner in this._usedFields.Select(f => f.DeclaringType).Distinct())
      if (this._cctors.TryGetValue(owner, out var cctorHandle))
        MethodLifter.ExtractStaticInitializers(this, cctorHandle, initializers);

    var globals = new List<IrGlobal>(this._usedFields.Count);
    foreach (var slot in this._usedFields) {
      initializers.TryGetValue(slot.Handle, out var init);
      if (init is null) {
        if (slot.Type.IsArray)
          init = new IrNewArray(slot.Type, new IrConst(IrType.I32, 0));
        else if (slot.Type.IsString)
          init = new IrStrConst("");
      }

      var global = new IrGlobal(slot.Name, slot.Type, init);
      this._globalsByField[slot.Handle] = global;
      globals.Add(global);
    }

    return globals;
  }

  private ResolvedCall ResolveMethodDef(MethodDefinitionHandle handle) {
    var def = this._metadata.GetMethodDefinition(handle);
    var typeDef = this._metadata.GetTypeDefinition(def.GetDeclaringType());
    var declaringType = MsilSignatureTypeProvider.FormatName(this._metadata.GetString(typeDef.Namespace), this._metadata.GetString(typeDef.Name));
    var methodName = this._metadata.GetString(def.Name);
    var sig = def.DecodeSignature(this._rootProvider, null);
    var isStatic = (def.Attributes & MethodAttributes.Static) != 0;
    return new(isStatic && this._allMethods.ContainsKey(handle), handle, declaringType, methodName, !isStatic, sig.ReturnType, sig.ParameterTypes);
  }

  private ResolvedCall ResolveMemberRef(MemberReferenceHandle handle, IReadOnlyList<MsilType>? methodGenericArgs) {
    var memberRef = this._metadata.GetMemberReference(handle);
    var methodName = this._metadata.GetString(memberRef.Name);
    var (declaringType, typeGenericArgs) = this.ResolveMemberParent(memberRef.Parent);
    var provider = typeGenericArgs is null && methodGenericArgs is null
      ? this._rootProvider
      : new MsilSignatureTypeProvider(typeGenericArgs, methodGenericArgs);
    var sig = memberRef.DecodeMethodSignature(provider, null);
    return new(false, default, declaringType, methodName, sig.Header.IsInstance, sig.ReturnType, sig.ParameterTypes);
  }

  private ResolvedCall ResolveMethodSpec(MethodSpecificationHandle handle) {
    var spec = this._metadata.GetMethodSpecification(handle);
    var methodArgs = spec.DecodeSignature(this._rootProvider, null);
    if (spec.Method.Kind == HandleKind.MemberReference)
      return this.ResolveMemberRef((MemberReferenceHandle)spec.Method, methodArgs);

    throw new MsilImportException(MsilFrontend.UnsupportedCall, "generic intra-assembly method calls are not supported");
  }

  private (string DeclaringType, IReadOnlyList<MsilType>? TypeGenericArgs) ResolveMemberParent(EntityHandle parent) {
    if (parent.Kind == HandleKind.TypeReference) {
      var tr = this._metadata.GetTypeReference((TypeReferenceHandle)parent);
      return (MsilSignatureTypeProvider.FormatName(this._metadata.GetString(tr.Namespace), this._metadata.GetString(tr.Name)), null);
    }

    if (parent.Kind == HandleKind.TypeSpecification) {
      var decoded = this._metadata.GetTypeSpecification((TypeSpecificationHandle)parent).DecodeSignature(this._rootProvider, null);
      if (decoded.IrType is { IsView: true, Element: { } elem }) {
        var genericName = decoded.IrType.Kind == IrTypeKind.Span ? "System.Span`1" : "System.ReadOnlySpan`1";
        return (genericName, [MsilType.FromIr(elem)]);
      }

      return (decoded.Name, null);
    }

    var td = this._metadata.GetTypeDefinition((TypeDefinitionHandle)parent);
    return (MsilSignatureTypeProvider.FormatName(this._metadata.GetString(td.Namespace), this._metadata.GetString(td.Name)), null);
  }

  private string MakeGlobalName(TypeDefinitionHandle typeHandle, string fieldName) {
    var typeName = this._metadata.GetString(this._metadata.GetTypeDefinition(typeHandle).Name);
    return SanitizeIdentifier($"{typeName}_{fieldName}");
  }

  private static string SanitizeIdentifier(string raw) {
    var sb = new StringBuilder(raw.Length);
    foreach (var ch in raw)
      sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');

    return sb.ToString();
  }

}
