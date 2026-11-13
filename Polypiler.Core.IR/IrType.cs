namespace Polypiler.Core.IR;

/// <summary>The shapes a value of the prototype IR can have.</summary>
public enum IrTypeKind {

  /// <summary>No value; only the return type of a function.</summary>
  Void,

  /// <summary>A truth value.</summary>
  Bool,

  /// <summary>An unsigned 8-bit integer. It is storage only: arithmetic happens on 32-bit values.</summary>
  U8,

  /// <summary>A signed 32-bit integer in two's complement that wraps around.</summary>
  I32,

  /// <summary>An unsigned 32-bit integer that wraps around.</summary>
  U32,

  /// <summary>A one-dimensional, zero-based array of integers with a fixed length.</summary>
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

  /// <summary>An unsigned 8-bit integer.</summary>
  public static IrType U8 { get; } = new(IrTypeKind.U8, null);

  /// <summary>A signed 32-bit integer.</summary>
  public static IrType I32 { get; } = new(IrTypeKind.I32, null);

  /// <summary>An unsigned 32-bit integer.</summary>
  public static IrType U32 { get; } = new(IrTypeKind.U32, null);

  /// <summary>The array type with the given element type.</summary>
  /// <param name="element">The type of every element.</param>
  /// <returns>The array type.</returns>
  public static IrType Array(IrType element) => new(IrTypeKind.Array, element);

  /// <summary>Whether values of the type are integers of any width.</summary>
  public bool IsInteger => this.Kind is IrTypeKind.U8 or IrTypeKind.I32 or IrTypeKind.U32;

  /// <summary>Whether the type is one of the two 32-bit types arithmetic is defined on.</summary>
  public bool IsWord => this.Kind is IrTypeKind.I32 or IrTypeKind.U32;

  /// <summary>Whether the type is an array.</summary>
  public bool IsArray => this.Kind == IrTypeKind.Array;

  /// <summary>Whether the type can be the type of a local: not void, and an array only of integers.</summary>
  public bool IsStorable => this.Kind != IrTypeKind.Void && (this.Element is null || this.Element.IsInteger);

  /// <summary>The name of the type as the IR writes it, for example <c>i32</c> or <c>u8[]</c>.</summary>
  public override string ToString()
    => this.Element is null ? this.Kind.ToString().ToLowerInvariant() : $"{this.Element}[]";

}
