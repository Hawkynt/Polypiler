using System;
using System.Collections.Generic;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Msil;

internal readonly record struct CellTypeHandles(TypeDefinitionHandle Type, FieldDefinitionHandle ValueField, MethodDefinitionHandle Ctor);

internal readonly record struct FnInterfaceHandles(TypeDefinitionHandle Type, MethodDefinitionHandle Invoke);

internal readonly record struct LambdaHandles(TypeDefinitionHandle Type, FieldDefinitionHandle[] CaptureFields, MethodDefinitionHandle Ctor, MethodDefinitionHandle Invoke);

internal readonly record struct FnRefHandles(TypeDefinitionHandle Type, MethodDefinitionHandle Ctor, MethodDefinitionHandle Invoke);

internal readonly record struct BoundMethodHandles(TypeDefinitionHandle Type, FieldDefinitionHandle? ReceiverField, MethodDefinitionHandle Ctor, MethodDefinitionHandle Invoke);

internal readonly record struct SeqInterfaceHandles(TypeDefinitionHandle Type, MethodDefinitionHandle Enumerate);

internal readonly record struct IterHandles(TypeDefinitionHandle Type, FieldDefinitionHandle[] ParamFields, FieldDefinitionHandle? ConsumedField, MethodDefinitionHandle Ctor, MethodDefinitionHandle Enumerate);

internal readonly record struct ArrSeqHandles(TypeDefinitionHandle Type, FieldDefinitionHandle ArrayField, MethodDefinitionHandle Ctor, MethodDefinitionHandle Enumerate);

internal readonly record struct SliceHandles(TypeDefinitionHandle Type, FieldDefinitionHandle ArrayField, FieldDefinitionHandle OffsetField, FieldDefinitionHandle LengthField);

internal readonly record struct PtrHandles(TypeDefinitionHandle Type, FieldDefinitionHandle BackingField, FieldDefinitionHandle ByteOffsetField, FieldDefinitionHandle RegionStartField, FieldDefinitionHandle RegionBytesField);

/// <summary>
/// Holds the <see cref="MetadataBuilder"/>, cached BCL type/member references, signature encoders,
/// and module-defined / synthesized type, field, and method handle maps for one emitted assembly.
/// </summary>
internal sealed class MsilMetadataContext {

  private static readonly byte[] _systemRuntimePublicKeyToken = [0xb0, 0x3f, 0x5f, 0x7f, 0x11, 0xd5, 0x0a, 0x3a];

  private readonly Dictionary<string, AssemblyReferenceHandle> _assemblyRefs = new(StringComparer.Ordinal);
  private readonly Dictionary<(string Assembly, string Namespace, string Name), TypeReferenceHandle> _typeRefs = new();
  private readonly Dictionary<string, MemberReferenceHandle> _memberRefs = new(StringComparer.Ordinal);
  private readonly Dictionary<string, MethodSpecificationHandle> _methodSpecs = new(StringComparer.Ordinal);
  private readonly Dictionary<string, ModuleReferenceHandle> _moduleRefs = new(StringComparer.Ordinal);
  private readonly Dictionary<string, TypeSpecificationHandle> _typeSpecs = new(StringComparer.Ordinal);

  public MsilMetadataContext(MetadataBuilder metadata, MsilProfile profile, MsilSynthesisPlan plan) {
    this.Metadata = metadata;
    this.Profile = profile;
    this.Plan = plan;
    this.SystemRuntime = this.GetAssemblyRef("System.Runtime");
  }

  public MetadataBuilder Metadata { get; }

  public MsilProfile Profile { get; }

  public MsilSynthesisPlan Plan { get; }

  public AssemblyReferenceHandle SystemRuntime { get; }

  public Dictionary<IrTypeDeclaration, TypeDefinitionHandle> DeclaredTypes { get; } = new(ReferenceEqualityComparer.Instance);

  public Dictionary<IrField, FieldDefinitionHandle> FieldHandles { get; } = new(ReferenceEqualityComparer.Instance);

  public Dictionary<IrConstructor, MethodDefinitionHandle> ClassCtorHandles { get; } = new(ReferenceEqualityComparer.Instance);

  public Dictionary<string, MethodDefinitionHandle> ClassCtorByFn { get; } = new(StringComparer.Ordinal);

  public Dictionary<IrMethod, MethodDefinitionHandle> ClassMethodHandles { get; } = new(ReferenceEqualityComparer.Instance);

  public Dictionary<IrModuleConst, FieldDefinitionHandle> ConstantFields { get; } = new(ReferenceEqualityComparer.Instance);

  public Dictionary<IrGlobal, FieldDefinitionHandle> GlobalFields { get; } = new(ReferenceEqualityComparer.Instance);

  public Dictionary<string, MethodDefinitionHandle> FunctionHandles { get; } = new(StringComparer.Ordinal);

  public Dictionary<string, IrFunction> FunctionsByName { get; } = new(StringComparer.Ordinal);

  public Dictionary<string, MethodDefinitionHandle> ForeignHandles { get; } = new(StringComparer.Ordinal);

  public Dictionary<string, IrForeignFunction> ForeignByName { get; } = new(StringComparer.Ordinal);

  public Dictionary<IrType, CellTypeHandles> CellTypes { get; } = new();

  public Dictionary<IrFunctionType, FnInterfaceHandles> FnInterfaces { get; } = new();

  public Dictionary<IrLambda, LambdaHandles> LambdaClasses { get; } = new(ReferenceEqualityComparer.Instance);

  public Dictionary<(string Function, IrFunctionType Signature), FnRefHandles> FnRefClasses { get; } = new();

  public Dictionary<BoundMethodKey, BoundMethodHandles> BoundMethodClasses { get; } = new();

  public Dictionary<IrType, SeqInterfaceHandles> SeqInterfaces { get; } = new();

  public Dictionary<IrFunction, IterHandles> IterClasses { get; } = new(ReferenceEqualityComparer.Instance);

  public Dictionary<IrType, ArrSeqHandles> ArrSeqClasses { get; } = new();

  public Dictionary<IrType, SliceHandles> SliceStructs { get; } = new();

  public PtrHandles PtrStruct { get; set; }

  public MethodDefinitionHandle Pext32Handle { get; set; }

  public MethodDefinitionHandle Pext64Handle { get; set; }

  public MethodDefinitionHandle Pdep32Handle { get; set; }

  public MethodDefinitionHandle Pdep64Handle { get; set; }

  public Dictionary<IrType, MethodDefinitionHandle> PtrLoadHandles { get; } = new();

  public Dictionary<IrType, MethodDefinitionHandle> PtrStoreHandles { get; } = new();

  public Dictionary<IrClassType, MethodDefinitionHandle> RecordEqualsHandles { get; } = new(ReferenceEqualityComparer.Instance);

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

  /// <summary>Gets the metadata token (<see cref="TypeReferenceHandle"/> or <see cref="TypeDefinitionHandle"/>) for any class.</summary>
  public EntityHandle GetClassToken(IrClassType cls)
    => cls.IsLibrary ? this.GetExceptionTypeRef(cls) : this.DeclaredTypes[cls];

  /// <summary>Gets the metadata token for any interface (mapping <see cref="IrInterfaceType.IsDisposable"/> to <c>System.IDisposable</c>).</summary>
  public EntityHandle GetInterfaceToken(IrInterfaceType iface)
    => iface.IsDisposable ? this.GetBclType("System", "IDisposable") : this.DeclaredTypes[iface];

  /// <summary>Gets the metadata token for a class or interface method (mapping <c>IDisposable.Dispose</c> to <c>System.IDisposable::Dispose</c>).</summary>
  public EntityHandle GetMethodToken(IrMethod method) {
    if (this.Plan.InterfaceMethodOwners.TryGetValue(method, out var iface) && iface.IsDisposable)
      return this.GetInstanceMethodRef("System", "IDisposable", "Dispose", IrType.Void);

    return this.ClassMethodHandles[method];
  }

  /// <summary>Gets or adds a cached <see cref="TypeSpecificationHandle"/>.</summary>
  public TypeSpecificationHandle GetTypeSpec(string key, Action<SignatureTypeEncoder> encode) {
    if (this._typeSpecs.TryGetValue(key, out var handle))
      return handle;

    var blob = new BlobBuilder();
    encode(new BlobEncoder(blob).TypeSpecificationSignature());
    handle = this.Metadata.AddTypeSpecification(this.Metadata.GetOrAddBlob(blob));
    this._typeSpecs[key] = handle;
    return handle;
  }

  /// <summary>Gets or adds a <see cref="TypeSpecificationHandle"/> for <c>System.Nullable&lt;T&gt;</c>.</summary>
  public TypeSpecificationHandle GetNullableSpec(IrType underlying)
    => this.GetTypeSpec($"Nullable<{underlying}>", enc => {
      var args = enc.GenericInstantiation(this.GetBclType("System", "Nullable`1"), 1, isValueType: true);
      this.EncodeType(args.AddArgument(), underlying);
    });

  /// <summary>Gets or adds a <see cref="TypeSpecificationHandle"/> for <c>System.Collections.Generic.List&lt;T&gt;</c>.</summary>
  public TypeSpecificationHandle GetListSpec(IrType element)
    => this.GetTypeSpec($"List<{element}>", enc => {
      var args = enc.GenericInstantiation(this.GetAssemblyType("System.Collections", "System.Collections.Generic", "List`1"), 1, isValueType: false);
      this.EncodeType(args.AddArgument(), element);
    });

  /// <summary>Gets or adds a <see cref="TypeSpecificationHandle"/> for <c>System.Collections.Generic.Dictionary&lt;K, V&gt;</c>.</summary>
  public TypeSpecificationHandle GetDictionarySpec(IrType key, IrType value)
    => this.GetTypeSpec($"Dict<{key},{value}>", enc => {
      var args = enc.GenericInstantiation(this.GetAssemblyType("System.Collections", "System.Collections.Generic", "Dictionary`2"), 2, isValueType: false);
      this.EncodeType(args.AddArgument(), key);
      this.EncodeType(args.AddArgument(), value);
    });

  /// <summary>Gets or adds a <see cref="TypeSpecificationHandle"/> for <c>System.Collections.Generic.HashSet&lt;T&gt;</c>.</summary>
  public TypeSpecificationHandle GetHashSetSpec(IrType element)
    => this.GetTypeSpec($"HashSet<{element}>", enc => {
      var args = enc.GenericInstantiation(this.GetAssemblyType("System.Collections", "System.Collections.Generic", "HashSet`1"), 1, isValueType: false);
      this.EncodeType(args.AddArgument(), element);
    });

  /// <summary>Gets or adds a <see cref="TypeSpecificationHandle"/> for <c>System.Collections.Generic.KeyValuePair&lt;K, V&gt;</c>.</summary>
  public TypeSpecificationHandle GetKeyValuePairSpec(IrType key, IrType value)
    => this.GetTypeSpec($"KVP<{key},{value}>", enc => {
      var args = enc.GenericInstantiation(this.GetBclType("System.Collections.Generic", "KeyValuePair`2"), 2, isValueType: true);
      this.EncodeType(args.AddArgument(), key);
      this.EncodeType(args.AddArgument(), value);
    });

  /// <summary>Gets or adds a <see cref="TypeSpecificationHandle"/> for <c>System.Collections.Generic.IEnumerable&lt;E&gt;</c>.</summary>
  public TypeSpecificationHandle GetIEnumerableSpec(string elemKey, Action<SignatureTypeEncoder> encodeElem)
    => this.GetTypeSpec($"IEnumerable<{elemKey}>", enc => {
      var args = enc.GenericInstantiation(this.GetBclType("System.Collections.Generic", "IEnumerable`1"), 1, isValueType: false);
      encodeElem(args.AddArgument());
    });

  /// <summary>Gets or adds a <see cref="TypeSpecificationHandle"/> for <c>System.Collections.Generic.IEnumerator&lt;E&gt;</c>.</summary>
  public TypeSpecificationHandle GetIEnumeratorSpec(string elemKey, Action<SignatureTypeEncoder> encodeElem)
    => this.GetTypeSpec($"IEnumerator<{elemKey}>", enc => {
      var args = enc.GenericInstantiation(this.GetBclType("System.Collections.Generic", "IEnumerator`1"), 1, isValueType: false);
      encodeElem(args.AddArgument());
    });

  /// <summary>Gets or adds a <see cref="TypeSpecificationHandle"/> for <c>System.Threading.Tasks.Task&lt;T&gt;</c>.</summary>
  public TypeSpecificationHandle GetTaskSpec(IrType element)
    => this.GetTypeSpec($"Task<{element}>", enc => {
      var args = enc.GenericInstantiation(this.GetBclType("System.Threading.Tasks", "Task`1"), 1, isValueType: false);
      this.EncodeType(args.AddArgument(), element);
    });

  /// <summary>Gets or adds a <see cref="TypeSpecificationHandle"/> for <c>System.Runtime.CompilerServices.TaskAwaiter&lt;T&gt;</c>.</summary>
  public TypeSpecificationHandle GetTaskAwaiterSpec(IrType element)
    => this.GetTypeSpec($"TaskAwaiter<{element}>", enc => {
      var args = enc.GenericInstantiation(this.GetBclType("System.Runtime.CompilerServices", "TaskAwaiter`1"), 1, isValueType: true);
      this.EncodeType(args.AddArgument(), element);
    });

  /// <summary>Gets or adds a type token (<see cref="EntityHandle"/>) for instructions like <c>newarr</c>, <c>initobj</c>, <c>ldelem</c>, <c>stelem</c>, <c>isinst</c>, <c>castclass</c>.</summary>
  public EntityHandle GetTypeToken(IrType type) {
    if (type.IsEnum)
      return this.DeclaredTypes[type.EnumType!];
    if (type.IsStruct)
      return this.DeclaredTypes[type.StructType!];
    if (type.IsOptional)
      return this.GetNullableSpec(type.Underlying!);
    if (type.IsObject)
      return type.ClassType is { } cls ? this.GetClassToken(cls) : this.GetInterfaceToken(type.InterfaceType!);
    if (type.IsFunction)
      return this.FnInterfaces[type.Signature!].Type;
    if (type.IsLazySequence)
      return this.SeqInterfaces[type.Element!].Type;
    if (type.IsView || type.IsMemory)
      return this.SliceStructs[type.Element!].Type;
    if (type.IsPointer)
      return this.PtrStruct.Type;
    if (type.Kind == IrTypeKind.List)
      return this.GetListSpec(type.Element!);
    if (type.Kind == IrTypeKind.Map)
      return this.GetDictionarySpec(type.Key!, type.Element!);
    if (type.Kind == IrTypeKind.Set)
      return this.GetHashSetSpec(type.Element!);
    if (type.IsAsync)
      return type.Element == IrType.Void ? this.GetBclType("System.Threading.Tasks", "Task") : this.GetTaskSpec(type.Element!);
    if (type.IsArray)
      return this.GetTypeSpec($"arr:{type}", enc => this.EncodeType(enc, type));

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
      case IrTypeKind.Optional: {
        var args = encoder.GenericInstantiation(this.GetBclType("System", "Nullable`1"), 1, isValueType: true);
        this.EncodeType(args.AddArgument(), type.Underlying!);
        break;
      }
      case IrTypeKind.Object: {
        var token = type.ClassType is { } cls ? this.GetClassToken(cls) : this.GetInterfaceToken(type.InterfaceType!);
        encoder.Type(token, isValueType: false);
        break;
      }
      case IrTypeKind.Function:
        encoder.Type(this.FnInterfaces[type.Signature!].Type, isValueType: false);
        break;
      case IrTypeKind.List: {
        var args = encoder.GenericInstantiation(this.GetAssemblyType("System.Collections", "System.Collections.Generic", "List`1"), 1, isValueType: false);
        this.EncodeType(args.AddArgument(), type.Element!);
        break;
      }
      case IrTypeKind.Map: {
        var args = encoder.GenericInstantiation(this.GetAssemblyType("System.Collections", "System.Collections.Generic", "Dictionary`2"), 2, isValueType: false);
        this.EncodeType(args.AddArgument(), type.Key!);
        this.EncodeType(args.AddArgument(), type.Element!);
        break;
      }
      case IrTypeKind.Set: {
        var args = encoder.GenericInstantiation(this.GetAssemblyType("System.Collections", "System.Collections.Generic", "HashSet`1"), 1, isValueType: false);
        this.EncodeType(args.AddArgument(), type.Element!);
        break;
      }
      case IrTypeKind.Sequence:
        encoder.Type(this.SeqInterfaces[type.Element!].Type, isValueType: false);
        break;
      case IrTypeKind.Async:
        if (type.Element == IrType.Void) {
          encoder.Type(this.GetBclType("System.Threading.Tasks", "Task"), isValueType: false);
        } else {
          var args = encoder.GenericInstantiation(this.GetBclType("System.Threading.Tasks", "Task`1"), 1, isValueType: false);
          this.EncodeType(args.AddArgument(), type.Element!);
        }
        break;
      case IrTypeKind.Span or IrTypeKind.ReadOnlySpan or IrTypeKind.Memory or IrTypeKind.ReadOnlyMemory:
        encoder.Type(this.SliceStructs[type.Element!].Type, isValueType: true);
        break;
      case IrTypeKind.Pointer:
        encoder.Type(this.PtrStruct.Type, isValueType: true);
        break;
    }
  }

  /// <summary>Encodes a field signature blob for <paramref name="type"/>.</summary>
  public BlobHandle EncodeFieldSignature(IrType type) {
    var blob = new BlobBuilder();
    this.EncodeType(new BlobEncoder(blob).FieldSignature(), type);
    return this.Metadata.GetOrAddBlob(blob);
  }

  /// <summary>Encodes a field signature blob using a custom type encoder.</summary>
  public BlobHandle EncodeCustomFieldSignature(Action<SignatureTypeEncoder> encode) {
    var blob = new BlobBuilder();
    encode(new BlobEncoder(blob).FieldSignature());
    return this.Metadata.GetOrAddBlob(blob);
  }

  /// <summary>Encodes a method signature blob for <paramref name="returnType"/> and <paramref name="parameters"/>.</summary>
  public BlobHandle EncodeMethodSignature(IrType returnType, IReadOnlyList<IrType> parameters, bool isInstance = false) {
    var blob = new BlobBuilder();
    new BlobEncoder(blob).MethodSignature(isInstanceMethod: isInstance).Parameters(
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

  /// <summary>Encodes a custom method signature blob.</summary>
  public BlobHandle EncodeCustomMethodSignature(bool isInstance, Action<ReturnTypeEncoder> encodeReturn, int paramCount, Action<ParametersEncoder> encodeParams) {
    var blob = new BlobBuilder();
    new BlobEncoder(blob).MethodSignature(isInstanceMethod: isInstance).Parameters(paramCount, encodeReturn, encodeParams);
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
  public StandaloneSignatureHandle EncodeLocals(IReadOnlyList<Action<SignatureTypeEncoder>> locals) {
    if (locals.Count == 0)
      return default;

    var blob = new BlobBuilder();
    var encoder = new BlobEncoder(blob).LocalVariableSignature(locals.Count);
    foreach (var encode in locals)
      encode(encoder.AddVariable().Type());

    return this.Metadata.AddStandaloneSignature(this.Metadata.GetOrAddBlob(blob));
  }

  /// <summary>Gets or adds a cached BCL <see cref="MemberReferenceHandle"/>.</summary>
  public MemberReferenceHandle GetMemberRef(string key, EntityHandle parent, string name, Action<MethodSignatureEncoder> encodeSignature, int genericParameterCount = 0) {
    if (this._memberRefs.TryGetValue(key, out var handle))
      return handle;

    var blob = new BlobBuilder();
    encodeSignature(new BlobEncoder(blob).MethodSignature(
      genericParameterCount: genericParameterCount,
      isInstanceMethod: key.StartsWith("inst:", StringComparison.Ordinal)));
    handle = this.Metadata.AddMemberReference(parent, this.Metadata.GetOrAddString(name), this.Metadata.GetOrAddBlob(blob));
    this._memberRefs[key] = handle;
    return handle;
  }

  /// <summary>Gets or adds a cached <see cref="MethodSpecificationHandle"/>.</summary>
  public MethodSpecificationHandle GetMethodSpec(string key, EntityHandle method, int argCount, Action<GenericTypeArgumentsEncoder> encodeArgs) {
    if (this._methodSpecs.TryGetValue(key, out var handle))
      return handle;

    var blob = new BlobBuilder();
    encodeArgs(new BlobEncoder(blob).MethodSpecificationSignature(argCount));
    handle = this.Metadata.AddMethodSpecification(method, this.Metadata.GetOrAddBlob(blob));
    this._methodSpecs[key] = handle;
    return handle;
  }

  /// <summary>Gets a cached <c>System.Object::.ctor()</c> member reference.</summary>
  public MemberReferenceHandle GetObjectCtor()
    => this.GetMemberRef("inst:System.Object..ctor", this.GetBclType("System", "Object"), ".ctor", sig => sig.Parameters(0, r => r.Void(), _ => { }));

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

  /// <summary>Gets a cached <c>Task.FromResult&lt;T&gt;(T)</c> method specification.</summary>
  public MethodSpecificationHandle GetTaskFromResult(IrType element) {
    var taskType = this.GetBclType("System.Threading.Tasks", "Task");
    var taskGeneric = this.GetBclType("System.Threading.Tasks", "Task`1");
    var member = this.GetMemberRef(
      "stat:Task::FromResult<T>(T)",
      taskType,
      "FromResult",
      sig => sig.Parameters(
        1,
        ret => ret.Type().GenericInstantiation(taskGeneric, 1, isValueType: false).AddArgument().GenericMethodTypeParameter(0),
        p => p.AddParameter().Type().GenericMethodTypeParameter(0)),
      genericParameterCount: 1);
    return this.GetMethodSpec($"Task.FromResult<{element}>", member, 1, args => this.EncodeType(args.AddArgument(), element));
  }

  /// <summary>Gets a cached <c>Task.FromException&lt;T&gt;(Exception)</c> method specification.</summary>
  public MethodSpecificationHandle GetTaskFromException(IrType element) {
    var taskType = this.GetBclType("System.Threading.Tasks", "Task");
    var taskGeneric = this.GetBclType("System.Threading.Tasks", "Task`1");
    var excType = this.GetBclType("System", "Exception");
    var member = this.GetMemberRef(
      "stat:Task::FromException<T>(Exception)",
      taskType,
      "FromException",
      sig => sig.Parameters(
        1,
        ret => ret.Type().GenericInstantiation(taskGeneric, 1, isValueType: false).AddArgument().GenericMethodTypeParameter(0),
        p => p.AddParameter().Type().Type(excType, isValueType: false)),
      genericParameterCount: 1);
    return this.GetMethodSpec($"Task.FromException<{element}>", member, 1, args => this.EncodeType(args.AddArgument(), element));
  }

  /// <summary>Gets a cached <c>Task.WhenAll&lt;T&gt;(Task&lt;T&gt;[])</c> method specification.</summary>
  public MethodSpecificationHandle GetTaskWhenAll(IrType element) {
    var taskType = this.GetBclType("System.Threading.Tasks", "Task");
    var taskGeneric = this.GetBclType("System.Threading.Tasks", "Task`1");
    var member = this.GetMemberRef(
      "stat:Task::WhenAll<T>(Task<T>[])",
      taskType,
      "WhenAll",
      sig => sig.Parameters(
        1,
        ret => ret.Type().GenericInstantiation(taskGeneric, 1, isValueType: false).AddArgument().SZArray().GenericMethodTypeParameter(0),
        p => p.AddParameter().Type().SZArray().GenericInstantiation(taskGeneric, 1, isValueType: false).AddArgument().GenericMethodTypeParameter(0)),
      genericParameterCount: 1);
    return this.GetMethodSpec($"Task.WhenAll<{element}>", member, 1, args => this.EncodeType(args.AddArgument(), element));
  }

  /// <summary>Gets a cached <c>Task.CompletedTask</c> getter reference.</summary>
  public MemberReferenceHandle GetTaskCompletedTask() {
    var taskType = this.GetBclType("System.Threading.Tasks", "Task");
    return this.GetMemberRef(
      "stat:Task::get_CompletedTask()",
      taskType,
      "get_CompletedTask",
      sig => sig.Parameters(0, r => r.Type().Type(taskType, isValueType: false), _ => { }));
  }

  /// <summary>Gets a cached <c>Task.FromException(Exception)</c> reference.</summary>
  public MemberReferenceHandle GetTaskFromExceptionVoid() {
    var taskType = this.GetBclType("System.Threading.Tasks", "Task");
    var excType = this.GetBclType("System", "Exception");
    return this.GetMemberRef(
      "stat:Task::FromException(Exception)",
      taskType,
      "FromException",
      sig => sig.Parameters(1, r => r.Type().Type(taskType, isValueType: false), p => p.AddParameter().Type().Type(excType, isValueType: false)));
  }

  /// <summary>Gets a cached <c>Task.WhenAll(Task[])</c> reference.</summary>
  public MemberReferenceHandle GetTaskWhenAllVoid() {
    var taskType = this.GetBclType("System.Threading.Tasks", "Task");
    return this.GetMemberRef(
      "stat:Task::WhenAll(Task[])",
      taskType,
      "WhenAll",
      sig => sig.Parameters(1, r => r.Type().Type(taskType, isValueType: false), p => p.AddParameter().Type().SZArray().Type(taskType, isValueType: false)));
  }

}
