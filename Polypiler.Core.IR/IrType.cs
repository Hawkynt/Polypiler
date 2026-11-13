namespace Polypiler.Core.IR;

/// <summary>The shapes a value of the prototype IR can have.</summary>
public enum IrTypeKind {

  /// <summary>No value; only the return type of a function.</summary>
  Void,

  /// <summary>A truth value.</summary>
  Bool,

  /// <summary>A signed 8-bit integer. It is storage only: arithmetic happens on 32-bit values.</summary>
  I8,

  /// <summary>An unsigned 8-bit integer. It is storage only: arithmetic happens on 32-bit values.</summary>
  U8,

  /// <summary>A signed 16-bit integer. It is storage only: arithmetic happens on 32-bit values.</summary>
  I16,

  /// <summary>An unsigned 16-bit integer; also a UTF-16 code unit. It is storage only, like the other narrow types.</summary>
  U16,

  /// <summary>A signed 32-bit integer in two's complement that wraps around.</summary>
  I32,

  /// <summary>An unsigned 32-bit integer that wraps around.</summary>
  U32,

  /// <summary>A signed 64-bit integer in two's complement that wraps around.</summary>
  I64,

  /// <summary>An unsigned 64-bit integer that wraps around.</summary>
  U64,

  /// <summary>A one-dimensional, zero-based array of numbers with a fixed length.</summary>
  Array

}

/// <summary>A type of the prototype IR. Two types are equal when kind and element type are.</summary>
public sealed record IrType {

  private IrType(IrTypeKind kind, IrType? element) {
    this.Kind = kind;
    this.Element = element;
  }

  /// <summary>The shape of the type.</summary>
  public IrTypeKind Kind { get; }

  /// <summary>The element type of an array, otherwise <see langword="null"/>.</summary>
  public IrType? Element { get; }

  /// <summary>No value.</summary>
  public static IrType Void { get; } = new(IrTypeKind.Void, null);

  /// <summary>A truth value.</summary>
  public static IrType Bool { get; } = new(IrTypeKind.Bool, null);

  /// <summary>A signed 8-bit integer.</summary>
  public static IrType I8 { get; } = new(IrTypeKind.I8, null);

  /// <summary>An unsigned 8-bit integer.</summary>
  public static IrType U8 { get; } = new(IrTypeKind.U8, null);

  /// <summary>A signed 16-bit integer.</summary>
  public static IrType I16 { get; } = new(IrTypeKind.I16, null);

  /// <summary>An unsigned 16-bit integer.</summary>
  public static IrType U16 { get; } = new(IrTypeKind.U16, null);

  /// <summary>A signed 32-bit integer.</summary>
  public static IrType I32 { get; } = new(IrTypeKind.I32, null);

  /// <summary>An unsigned 32-bit integer.</summary>
  public static IrType U32 { get; } = new(IrTypeKind.U32, null);

  /// <summary>A signed 64-bit integer.</summary>
  public static IrType I64 { get; } = new(IrTypeKind.I64, null);

  /// <summary>An unsigned 64-bit integer.</summary>
  public static IrType U64 { get; } = new(IrTypeKind.U64, null);

  /// <summary>The array type with the given element type.</summary>
  /// <param name="element">The type of every element.</param>
  /// <returns>The array type.</returns>
  public static IrType Array(IrType element) => new(IrTypeKind.Array, element);

  /// <summary>Whether values of the type are integers of any width.</summary>
  public bool IsInteger => this.Kind is >= IrTypeKind.I8 and <= IrTypeKind.U64;

  /// <summary>Whether the type is one of the two 32-bit integer types.</summary>
  public bool IsWord => this.Kind is IrTypeKind.I32 or IrTypeKind.U32;

  /// <summary>Whether the type is one of the two 64-bit integer types.</summary>
  public bool IsLong => this.Kind is IrTypeKind.I64 or IrTypeKind.U64;

  /// <summary>Whether integer arithmetic is defined on the type: it is an integer of 32 or 64 bits.</summary>
  public bool IsWideInteger => this.IsWord || this.IsLong;

  /// <summary>Whether the type is an integer that has negative values.</summary>
  public bool IsSigned => this.Kind is IrTypeKind.I8 or IrTypeKind.I16 or IrTypeKind.I32 or IrTypeKind.I64;

  /// <summary>The width of an integer type in bits; 0 for every other type.</summary>
  public int Bits => this.Kind switch {
    IrTypeKind.I8 or IrTypeKind.U8 => 8,
    IrTypeKind.I16 or IrTypeKind.U16 => 16,
    IrTypeKind.I32 or IrTypeKind.U32 => 32,
    IrTypeKind.I64 or IrTypeKind.U64 => 64,
    _ => 0
  };

  /// <summary>Whether values of the type are numbers.</summary>
  public bool IsNumeric => this.IsInteger;

  /// <summary>Whether arithmetic is defined on the type; the narrow integers are widened first.</summary>
  public bool IsArithmetic => this.IsWideInteger;

  /// <summary>Whether the type is an array.</summary>
  public bool IsArray => this.Kind == IrTypeKind.Array;

  /// <summary>Whether the type can be the type of a local: not void, and an array only of numbers.</summary>
  public bool IsStorable => this.Kind != IrTypeKind.Void && (this.Element is null || this.Element.IsNumeric);

  /// <summary>The name of the type as the IR writes it, for example <c>i32</c> or <c>u8[]</c>.</summary>
  public override string ToString()
    => this.Element is null ? this.Kind.ToString().ToLowerInvariant() : $"{this.Element}[]";

}
