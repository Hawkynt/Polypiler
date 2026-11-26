using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Msil;

/// <summary>
/// Holds the <see cref="MetadataBuilder"/>, cached BCL type/member references, signature encoders,
/// and module-defined type/field/method handle maps for one emitted assembly.
/// </summary>
internal sealed class MsilMetadataContext {

  private static readonly byte[] _systemRuntimePublicKeyToken = [0xb0, 0x3f, 0x5f, 0x7f, 0x11, 0xd5, 0x0a, 0x3a];

  private readonly Dictionary<string, AssemblyReferenceHandle> _assemblyRefs = new(StringComparer.Ordinal);
  private readonly Dictionary<(string Assembly, string Namespace, string Name), TypeReferenceHandle> _typeRefs = new();
  private readonly Dictionary<string, MemberReferenceHandle> _memberRefs = new(StringComparer.Ordinal);
  private readonly Dictionary<string, ModuleReferenceHandle> _moduleRefs = new(StringComparer.Ordinal);
  private readonly Dictionary<IrType, TypeSpecificationHandle> _nullableSpecs = new();
  private readonly Dictionary<IrType, TypeSpecificationHandle> _arraySpecs = new();

  public MsilMetadataContext(MetadataBuilder metadata, MsilProfile profile) {
    this.Metadata = metadata;
    this.Profile = profile;
    this.SystemRuntime = this.GetAssemblyRef("System.Runtime");
  }

  public MetadataBuilder Metadata { get; }

  public MsilProfile Profile { get; }

  public AssemblyReferenceHandle SystemRuntime { get; }

  public Dictionary<IrTypeDeclaration, TypeDefinitionHandle> DeclaredTypes { get; } = new(ReferenceEqualityComparer.Instance);

  public Dictionary<IrField, FieldDefinitionHandle> StructFields { get; } = new(ReferenceEqualityComparer.Instance);

  public Dictionary<IrModuleConst, FieldDefinitionHandle> ConstantFields { get; } = new(ReferenceEqualityComparer.Instance);

  public Dictionary<IrGlobal, FieldDefinitionHandle> GlobalFields { get; } = new(ReferenceEqualityComparer.Instance);

  public Dictionary<string, MethodDefinitionHandle> FunctionHandles { get; } = new(StringComparer.Ordinal);

  public Dictionary<string, IrFunction> FunctionsByName { get; } = new(StringComparer.Ordinal);

  public Dictionary<string, MethodDefinitionHandle> ForeignHandles { get; } = new(StringComparer.Ordinal);

  public Dictionary<string, IrForeignFunction> ForeignByName { get; } = new(StringComparer.Ordinal);

  /// <summary>Gets or adds a cached <see cref="AssemblyReferenceHandle"/> for a .NET BCL assembly.</summary>
  public AssemblyReferenceHandle GetAssemblyRef(string assemblyName) {
    if (this._assemblyRefs.TryGetValue(assemblyName, out var handle))
      return handle;

    handle = this.Metadata.AddAssemblyReference(
      name: this.Metadata.GetOrAddString(assemblyName),
      version: this.Profile.RuntimeVersion,
      culture: default,
      publicKeyOrToken: this.Metadata.GetOrAddBlob(_systemRuntimePublicKeyToken),
      flags: 0,
      hashValue: default);
    this._assemblyRefs[assemblyName] = handle;
    return handle;
  }

  /// <summary>Gets or adds a cached <see cref="ModuleReferenceHandle"/> for a P/Invoke native library.</summary>
  public ModuleReferenceHandle GetModuleRef(string library) {
    if (this._moduleRefs.TryGetValue(library, out var handle))
      return handle;

    handle = this.Metadata.AddModuleReference(this.Metadata.GetOrAddString(library));
    this._moduleRefs[library] = handle;
    return handle;
  }

  /// <summary>Gets or adds a <see cref="TypeReferenceHandle"/> in <c>System.Runtime</c>.</summary>
  public TypeReferenceHandle GetBclType(string @namespace, string name)
    => this.GetAssemblyType("System.Runtime", @namespace, name);

  /// <summary>Gets or adds a <see cref="TypeReferenceHandle"/> in <paramref name="assemblyName"/>.</summary>
  public TypeReferenceHandle GetAssemblyType(string assemblyName, string @namespace, string name) {
    var key = (assemblyName, @namespace, name);
    if (this._typeRefs.TryGetValue(key, out var handle))
      return handle;

    handle = this.Metadata.AddTypeReference(
      this.GetAssemblyRef(assemblyName),
      this.Metadata.GetOrAddString(@namespace),
      this.Metadata.GetOrAddString(name));
    this._typeRefs[key] = handle;
    return handle;
  }

  /// <summary>Maps an IR library exception class to its .NET <c>System.Runtime</c> type reference.</summary>
  public TypeReferenceHandle GetExceptionTypeRef(IrClassType cls) {
    if (ReferenceEquals(cls, IrExceptionClass.KeyNotFoundException))
      return this.GetBclType("System.Collections.Generic", "KeyNotFoundException");
    if (ReferenceEquals(cls, IrExceptionClass.AssertionException))
      return this.GetBclType("System", "Exception");

    return this.GetBclType("System", cls.Name);
  }

  /// <summary>Gets or adds a <see cref="TypeSpecificationHandle"/> for <c>System.Nullable&lt;T&gt;</c>.</summary>
  public TypeSpecificationHandle GetNullableSpec(IrType underlying) {
    if (this._nullableSpecs.TryGetValue(underlying, out var handle))
      return handle;

    var blob = new BlobBuilder();
    var typeEncoder = new BlobEncoder(blob).TypeSpecificationSignature();
    var args = typeEncoder.GenericInstantiation(this.GetBclType("System", "Nullable`1"), 1, isValueType: true);
    this.EncodeType(args.AddArgument(), underlying);
    handle = this.Metadata.AddTypeSpecification(this.Metadata.GetOrAddBlob(blob));
    this._nullableSpecs[underlying] = handle;
    return handle;
  }

  /// <summary>Gets or adds a type token (<see cref="EntityHandle"/>) for instructions like <c>newarr</c>, <c>initobj</c>, <c>ldelem</c>, <c>stelem</c>, <c>ldelema</c>.</summary>
  public EntityHandle GetTypeToken(IrType type) {
    if (type.IsEnum)
      return this.DeclaredTypes[type.EnumType!];
    if (type.IsStruct)
      return this.DeclaredTypes[type.StructType!];
    if (type.IsOptional)
      return this.GetNullableSpec(type.Underlying!);
    if (type.IsObject)
      return this.GetExceptionTypeRef(type.ClassType!);
    if (type.IsArray) {
      if (this._arraySpecs.TryGetValue(type, out var spec))
        return spec;

      var blob = new BlobBuilder();
      this.EncodeType(new BlobEncoder(blob).TypeSpecificationSignature(), type);
      spec = this.Metadata.AddTypeSpecification(this.Metadata.GetOrAddBlob(blob));
      this._arraySpecs[type] = spec;
      return spec;
    }

    return type.Kind switch {
      IrTypeKind.Bool => this.GetBclType("System", "Boolean"),
      IrTypeKind.I8 => this.GetBclType("System", "SByte"),
      IrTypeKind.U8 => this.GetBclType("System", "Byte"),
      IrTypeKind.I16 => this.GetBclType("System", "Int16"),
      IrTypeKind.U16 => this.GetBclType("System", "UInt16"),
      IrTypeKind.I32 => this.GetBclType("System", "Int32"),
      IrTypeKind.U32 => this.GetBclType("System", "UInt32"),
      IrTypeKind.I64 => this.GetBclType("System", "Int64"),
      IrTypeKind.U64 => this.GetBclType("System", "UInt64"),
      IrTypeKind.F32 => this.GetBclType("System", "Single"),
      IrTypeKind.F64 => this.GetBclType("System", "Double"),
      _ => this.GetBclType("System", "String")
    };
  }

  /// <summary>Encodes an <see cref="IrType"/> into a metadata signature.</summary>
  public void EncodeType(SignatureTypeEncoder encoder, IrType type) {
    if (type.IsEnum) {
      encoder.Type(this.DeclaredTypes[type.EnumType!], isValueType: true);
      return;
    }

    switch (type.Kind) {
      case IrTypeKind.Bool:
        encoder.Boolean();
        break;
      case IrTypeKind.I8:
        encoder.SByte();
        break;
      case IrTypeKind.U8:
        encoder.Byte();
        break;
      case IrTypeKind.I16:
        encoder.Int16();
        break;
      case IrTypeKind.U16:
        encoder.UInt16();
        break;
      case IrTypeKind.I32:
        encoder.Int32();
        break;
      case IrTypeKind.U32:
        encoder.UInt32();
        break;
      case IrTypeKind.I64:
        encoder.Int64();
        break;
      case IrTypeKind.U64:
        encoder.UInt64();
        break;
      case IrTypeKind.F32:
        encoder.Single();
        break;
      case IrTypeKind.F64:
        encoder.Double();
        break;
      case IrTypeKind.String:
        encoder.String();
        break;
      case IrTypeKind.Array:
        this.EncodeType(encoder.SZArray(), type.Element!);
        break;
      case IrTypeKind.Struct:
        encoder.Type(this.DeclaredTypes[type.StructType!], isValueType: true);
        break;
      case IrTypeKind.Optional:
        var args = encoder.GenericInstantiation(this.GetBclType("System", "Nullable`1"), 1, isValueType: true);
        this.EncodeType(args.AddArgument(), type.Underlying!);
        break;
      case IrTypeKind.Object:
        encoder.Type(this.GetExceptionTypeRef(type.ClassType!), isValueType: false);
        break;
    }
  }

  /// <summary>Encodes a field signature blob for <paramref name="type"/>.</summary>
  public BlobHandle EncodeFieldSignature(IrType type) {
    var blob = new BlobBuilder();
    this.EncodeType(new BlobEncoder(blob).FieldSignature(), type);
    return this.Metadata.GetOrAddBlob(blob);
  }

  /// <summary>Encodes a static method signature blob for <paramref name="returnType"/> and <paramref name="parameters"/>.</summary>
  public BlobHandle EncodeMethodSignature(IrType returnType, IReadOnlyList<IrType> parameters) {
    var blob = new BlobBuilder();
    new BlobEncoder(blob).MethodSignature().Parameters(
      parameters.Count,
      ret => {
        if (returnType == IrType.Void)
          ret.Void();
        else
          this.EncodeType(ret.Type(), returnType);
      },
      p => {
        foreach (var param in parameters)
          this.EncodeType(p.AddParameter().Type(), param);
      });
    return this.Metadata.GetOrAddBlob(blob);
  }

  /// <summary>Encodes a P/Invoke method signature blob for <paramref name="foreign"/>.</summary>
  public BlobHandle EncodeForeignMethodSignature(IrForeignFunction foreign) {
    var blob = new BlobBuilder();
    new BlobEncoder(blob).MethodSignature().Parameters(
      foreign.Parameters.Count,
      ret => {
        if (foreign.ReturnType == IrType.Void)
          ret.Void();
        else
          this.EncodeForeignType(ret.Type(), foreign.ReturnType, foreign.Result);
      },
      p => {
        foreach (var param in foreign.Parameters) {
          if (param.Marshal.Kind == IrMarshalKind.ByRef)
            this.EncodeType(p.AddParameter().Type(isByRef: true), param.Type.Element!);
          else
            this.EncodeForeignType(p.AddParameter().Type(), param.Type, param.Marshal);
        }
      });
    return this.Metadata.GetOrAddBlob(blob);
  }

  private void EncodeForeignType(SignatureTypeEncoder encoder, IrType type, IrMarshal marshal) {
    if (marshal.Kind == IrMarshalKind.Handle || (marshal.NativeSize && type.IsSigned))
      encoder.IntPtr();
    else if (marshal.NativeSize)
      encoder.UIntPtr();
    else
      this.EncodeType(encoder, type);
  }

  /// <summary>Encodes a local variables standalone signature handle, or <see langword="default"/> when empty.</summary>
  public StandaloneSignatureHandle EncodeLocals(IReadOnlyList<IrType> locals) {
    if (locals.Count == 0)
      return default;

    var blob = new BlobBuilder();
    var encoder = new BlobEncoder(blob).LocalVariableSignature(locals.Count);
    foreach (var local in locals)
      this.EncodeType(encoder.AddVariable().Type(), local);

    return this.Metadata.AddStandaloneSignature(this.Metadata.GetOrAddBlob(blob));
  }

  /// <summary>Gets or adds a cached BCL <see cref="MemberReferenceHandle"/>.</summary>
  public MemberReferenceHandle GetMemberRef(string key, EntityHandle parent, string name, Action<MethodSignatureEncoder> encodeSignature) {
    if (this._memberRefs.TryGetValue(key, out var handle))
      return handle;

    var blob = new BlobBuilder();
    encodeSignature(new BlobEncoder(blob).MethodSignature(isInstanceMethod: key.StartsWith("inst:", StringComparison.Ordinal)));
    handle = this.Metadata.AddMemberReference(parent, this.Metadata.GetOrAddString(name), this.Metadata.GetOrAddBlob(blob));
    this._memberRefs[key] = handle;
    return handle;
  }

  /// <summary>Gets a cached static BCL method reference taking the given IR types.</summary>
  public MemberReferenceHandle GetStaticMethodRef(string @namespace, string typeName, string methodName, IrType returnType, params IrType[] paramTypes) {
    var key = $"stat:{@namespace}.{typeName}::{methodName}({string.Join(",", (IEnumerable<IrType>)paramTypes)})->{returnType}";
    return this.GetMemberRef(key, this.GetBclType(@namespace, typeName), methodName, sig => sig.Parameters(
      paramTypes.Length,
      ret => {
        if (returnType == IrType.Void)
          ret.Void();
        else
          this.EncodeType(ret.Type(), returnType);
      },
      p => {
        foreach (var pt in paramTypes)
          this.EncodeType(p.AddParameter().Type(), pt);
      }));
  }

  /// <summary>Gets a cached instance BCL method reference taking the given IR types.</summary>
  public MemberReferenceHandle GetInstanceMethodRef(string @namespace, string typeName, string methodName, IrType returnType, params IrType[] paramTypes) {
    var key = $"inst:{@namespace}.{typeName}::{methodName}({string.Join(",", (IEnumerable<IrType>)paramTypes)})->{returnType}";
    return this.GetMemberRef(key, this.GetBclType(@namespace, typeName), methodName, sig => sig.Parameters(
      paramTypes.Length,
      ret => {
        if (returnType == IrType.Void)
          ret.Void();
        else
          this.EncodeType(ret.Type(), returnType);
      },
      p => {
        foreach (var pt in paramTypes)
          this.EncodeType(p.AddParameter().Type(), pt);
      }));
  }

  /// <summary>Gets a cached <c>Nullable&lt;T&gt;..ctor(!0)</c> member reference.</summary>
  public MemberReferenceHandle GetNullableCtor(IrType underlying) {
    var spec = this.GetNullableSpec(underlying);
    return this.GetMemberRef($"inst:Nullable<{underlying}>..ctor", spec, ".ctor", sig => sig.Parameters(
      1,
      ret => ret.Void(),
      p => p.AddParameter().Type().GenericTypeParameter(0)));
  }

  /// <summary>Gets a cached <c>Nullable&lt;T&gt;.get_HasValue()</c> member reference.</summary>
  public MemberReferenceHandle GetNullableHasValue(IrType underlying) {
    var spec = this.GetNullableSpec(underlying);
    return this.GetMemberRef($"inst:Nullable<{underlying}>.get_HasValue", spec, "get_HasValue", sig => sig.Parameters(
      0,
      ret => ret.Type().Boolean(),
      _ => { }));
  }

  /// <summary>Gets a cached <c>Nullable&lt;T&gt;.get_Value()</c> member reference.</summary>
  public MemberReferenceHandle GetNullableValue(IrType underlying) {
    var spec = this.GetNullableSpec(underlying);
    return this.GetMemberRef($"inst:Nullable<{underlying}>.get_Value", spec, "get_Value", sig => sig.Parameters(
      0,
      ret => ret.Type().GenericTypeParameter(0),
      _ => { }));
  }

  /// <summary>Gets a cached constructor reference for a library exception class with <paramref name="arity"/> parameters (0, 1, or 2).</summary>
  public MemberReferenceHandle GetExceptionCtor(IrClassType cls, int arity) {
    var typeRef = this.GetExceptionTypeRef(cls);
    var key = $"inst:exc:{cls.Name}..ctor/{arity}";
    return this.GetMemberRef(key, typeRef, ".ctor", sig => sig.Parameters(
      arity,
      ret => ret.Void(),
      p => {
        if (arity >= 1)
          p.AddParameter().Type().String();
        if (arity >= 2)
          p.AddParameter().Type().Type(this.GetBclType("System", "Exception"), isValueType: false);
      }));
  }

}
