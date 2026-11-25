using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Msil;

/// <summary>Kind of a decoded MSIL signature or stack type.</summary>
internal enum MsilTypeKind {
  Ir,
  Null,
  ByRef,
  Marker,
  Unsupported
}

/// <summary>
/// A type produced by decoding CLI metadata signatures or simulating the MSIL evaluation stack.
/// </summary>
internal sealed record MsilType {

  private static readonly Dictionary<string, IrClassType> _exceptionClasses = new(StringComparer.Ordinal) {
    ["System.Exception"] = IrExceptionClass.Exception,
    ["System.ArithmeticException"] = IrExceptionClass.ArithmeticException,
    ["System.DivideByZeroException"] = IrExceptionClass.DivideByZeroException,
    ["System.OverflowException"] = IrExceptionClass.OverflowException,
    ["System.IndexOutOfRangeException"] = IrExceptionClass.IndexOutOfRangeException,
    ["System.NullReferenceException"] = IrExceptionClass.NullReferenceException,
    ["System.InvalidCastException"] = IrExceptionClass.InvalidCastException,
    ["System.InvalidOperationException"] = IrExceptionClass.InvalidOperationException,
    ["System.ArgumentException"] = IrExceptionClass.ArgumentException,
    ["System.ArgumentOutOfRangeException"] = IrExceptionClass.ArgumentOutOfRangeException,
    ["System.Collections.Generic.KeyNotFoundException"] = IrExceptionClass.KeyNotFoundException,
    ["System.FormatException"] = IrExceptionClass.FormatException
  };

  private MsilType(MsilTypeKind kind, IrType? irType, string name) {
    this.Kind = kind;
    this.IrType = irType;
    this.Name = name;
  }

  /// <summary>The classification of this type.</summary>
  public MsilTypeKind Kind { get; }

  /// <summary>The underlying IR type when <see cref="Kind"/> is <see cref="MsilTypeKind.Ir"/>.</summary>
  public IrType? IrType { get; }

  /// <summary>Display or full CLR name for markers and diagnostics.</summary>
  public string Name { get; }

  /// <summary>The untyped null reference produced by <c>ldnull</c>.</summary>
  public static MsilType Null { get; } = new(MsilTypeKind.Null, null, "null");

  /// <summary>Wraps a supported PolyIR type.</summary>
  public static MsilType FromIr(IrType irType) => new(MsilTypeKind.Ir, irType, irType.ToString());

  /// <summary>A managed pointer (<c>T&amp;</c>) to an element type.</summary>
  public static MsilType ByRef(MsilType element) => new(MsilTypeKind.ByRef, null, $"{element.Name}&");

  /// <summary>A recognized BCL reference marker type (such as <c>System.Array</c> or <c>System.RuntimeFieldHandle</c>).</summary>
  public static MsilType Marker(string fullName) => new(MsilTypeKind.Marker, null, fullName);

  /// <summary>An unsupported CLR type that will emit <see cref="MsilFrontend.UnsupportedType"/> if used.</summary>
  public static MsilType Unsupported(string description) => new(MsilTypeKind.Unsupported, null, description);

  /// <summary>Returns the IR type or throws <see cref="MsilFrontend.UnsupportedType"/>.</summary>
  public IrType RequireIr(string context) {
    if (this.Kind == MsilTypeKind.Ir)
      return this.IrType!;

    throw new MsilImportException(MsilFrontend.UnsupportedType, $"unsupported CLR type '{this.Name}' in {context}");
  }

  /// <summary>Maps a well-known <c>System.*Exception</c> full name to its IR library class, if any.</summary>
  public static IrClassType? TryResolveExceptionClass(string fullName)
    => _exceptionClasses.GetValueOrDefault(fullName);

}

/// <summary>
/// <see cref="ISignatureTypeProvider{TType, TGenericContext}"/> decoding ECMA-335 signatures into <see cref="MsilType"/>.
/// </summary>
internal sealed class MsilSignatureTypeProvider(IReadOnlyList<MsilType>? typeArgs = null, IReadOnlyList<MsilType>? methodArgs = null)
  : ISignatureTypeProvider<MsilType, object?> {

  private static readonly Dictionary<PrimitiveTypeCode, MsilType> _primitives = new() {
    [PrimitiveTypeCode.Void] = MsilType.FromIr(IrType.Void),
    [PrimitiveTypeCode.Boolean] = MsilType.FromIr(IrType.Bool),
    [PrimitiveTypeCode.SByte] = MsilType.FromIr(IrType.I8),
    [PrimitiveTypeCode.Byte] = MsilType.FromIr(IrType.U8),
    [PrimitiveTypeCode.Int16] = MsilType.FromIr(IrType.I16),
    [PrimitiveTypeCode.UInt16] = MsilType.FromIr(IrType.U16),
    [PrimitiveTypeCode.Char] = MsilType.FromIr(IrType.U16),
    [PrimitiveTypeCode.Int32] = MsilType.FromIr(IrType.I32),
    [PrimitiveTypeCode.IntPtr] = MsilType.FromIr(IrType.I32),
    [PrimitiveTypeCode.UInt32] = MsilType.FromIr(IrType.U32),
    [PrimitiveTypeCode.UIntPtr] = MsilType.FromIr(IrType.U32),
    [PrimitiveTypeCode.Int64] = MsilType.FromIr(IrType.I64),
    [PrimitiveTypeCode.UInt64] = MsilType.FromIr(IrType.U64),
    [PrimitiveTypeCode.Single] = MsilType.FromIr(IrType.F32),
    [PrimitiveTypeCode.Double] = MsilType.FromIr(IrType.F64),
    [PrimitiveTypeCode.String] = MsilType.FromIr(IrType.String),
    [PrimitiveTypeCode.Object] = MsilType.Marker("System.Object")
  };

  private static readonly Dictionary<string, MsilType> _namedTypes = new(StringComparer.Ordinal) {
    ["System.Void"] = MsilType.FromIr(IrType.Void),
    ["System.Boolean"] = MsilType.FromIr(IrType.Bool),
    ["System.SByte"] = MsilType.FromIr(IrType.I8),
    ["System.Byte"] = MsilType.FromIr(IrType.U8),
    ["System.Int16"] = MsilType.FromIr(IrType.I16),
    ["System.UInt16"] = MsilType.FromIr(IrType.U16),
    ["System.Char"] = MsilType.FromIr(IrType.U16),
    ["System.Int32"] = MsilType.FromIr(IrType.I32),
    ["System.IntPtr"] = MsilType.FromIr(IrType.I32),
    ["System.UInt32"] = MsilType.FromIr(IrType.U32),
    ["System.UIntPtr"] = MsilType.FromIr(IrType.U32),
    ["System.Int64"] = MsilType.FromIr(IrType.I64),
    ["System.UInt64"] = MsilType.FromIr(IrType.U64),
    ["System.Single"] = MsilType.FromIr(IrType.F32),
    ["System.Double"] = MsilType.FromIr(IrType.F64),
    ["System.String"] = MsilType.FromIr(IrType.String),
    ["System.MidpointRounding"] = MsilType.FromIr(IrType.I32),
    ["System.Span`1"] = MsilType.Marker("System.Span`1"),
    ["System.ReadOnlySpan`1"] = MsilType.Marker("System.ReadOnlySpan`1"),
    ["System.Array"] = MsilType.Marker("System.Array"),
    ["System.Object"] = MsilType.Marker("System.Object"),
    ["System.RuntimeFieldHandle"] = MsilType.Marker("System.RuntimeFieldHandle")
  };

  private readonly IReadOnlyList<MsilType>? _typeArgs = typeArgs;
  private readonly IReadOnlyList<MsilType>? _methodArgs = methodArgs;

  public MsilType GetPrimitiveType(PrimitiveTypeCode typeCode)
    => _primitives.TryGetValue(typeCode, out var mapped) ? mapped : MsilType.Unsupported(typeCode.ToString());

  public MsilType GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) {
    var def = reader.GetTypeDefinition(handle);
    return FromNamedType(FormatName(reader.GetString(def.Namespace), reader.GetString(def.Name)));
  }

  public MsilType GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) {
    var reference = reader.GetTypeReference(handle);
    return FromNamedType(FormatName(reader.GetString(reference.Namespace), reader.GetString(reference.Name)));
  }

  public MsilType GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
    => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

  public MsilType GetSZArrayType(MsilType elementType) {
    if (elementType.IrType is { } ir && IrType.Array(ir) is { IsStorable: true } arr)
      return MsilType.FromIr(arr);

    return MsilType.Unsupported($"{elementType.Name}[]");
  }

  public MsilType GetArrayType(MsilType elementType, ArrayShape shape)
    => MsilType.Unsupported($"{elementType.Name}[rank={shape.Rank}]");

  public MsilType GetByReferenceType(MsilType elementType) => MsilType.ByRef(elementType);

  public MsilType GetPointerType(MsilType elementType) => MsilType.Unsupported($"{elementType.Name}*");

  public MsilType GetFunctionPointerType(MethodSignature<MsilType> signature) => MsilType.Unsupported("fnptr");

  public MsilType GetGenericInstantiation(MsilType genericType, ImmutableArray<MsilType> typeArguments) {
    if (typeArguments.Length == 1 && typeArguments[0].IrType is { IsNumeric: true } elem) {
      if (genericType.Name == "System.Span`1")
        return MsilType.FromIr(IrType.Span(elem));

      if (genericType.Name == "System.ReadOnlySpan`1")
        return MsilType.FromIr(IrType.ReadOnlySpan(elem));
    }

    return MsilType.Unsupported($"{genericType.Name}<{string.Join(", ", typeArguments)}>");
  }

  public MsilType GetGenericTypeParameter(object? genericContext, int index)
    => this._typeArgs is not null && index < this._typeArgs.Count ? this._typeArgs[index] : MsilType.Unsupported($"!{index}");

  public MsilType GetGenericMethodParameter(object? genericContext, int index)
    => this._methodArgs is not null && index < this._methodArgs.Count ? this._methodArgs[index] : MsilType.Unsupported($"!!{index}");

  public MsilType GetModifiedType(MsilType modifier, MsilType unmodifiedType, bool isRequired) => unmodifiedType;

  public MsilType GetPinnedType(MsilType elementType) => elementType;

  private static MsilType FromNamedType(string fullName) {
    if (_namedTypes.TryGetValue(fullName, out var mapped))
      return mapped;

    if (MsilType.TryResolveExceptionClass(fullName) is { } exceptionClass)
      return MsilType.FromIr(IrType.Object(exceptionClass));

    return MsilType.Unsupported(fullName);
  }

  internal static string FormatName(string ns, string name)
    => string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";

}
