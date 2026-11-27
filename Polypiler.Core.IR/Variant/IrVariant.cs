using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>
/// The tag of a value of the dynamic type <see cref="IrType.Variant"/>: which kind of value it holds.
/// The numbers are those of the COM <c>VARENUM</c> (<c>VT_EMPTY</c>, <c>VT_NULL</c>, <c>VT_I2</c>, …),
/// which Visual Basic's <c>VarType</c> returns and every Automation client agrees on.
/// </summary>
public enum IrVariantTag {

  /// <summary>No value yet (<c>VT_EMPTY</c>): what a variable of the type holds before it is assigned. It has no payload.</summary>
  Empty = 0,

  /// <summary>A value known to be missing (<c>VT_NULL</c>), as SQL's <c>NULL</c>. It has no payload.</summary>
  Null = 1,

  /// <summary>A signed 16-bit integer (<c>VT_I2</c>), payload <c>i16</c>.</summary>
  I16 = 2,

  /// <summary>A signed 32-bit integer (<c>VT_I4</c>), payload <c>i32</c>.</summary>
  I32 = 3,

  /// <summary>A binary32 number (<c>VT_R4</c>), payload <c>f32</c>.</summary>
  F32 = 4,

  /// <summary>A binary64 number (<c>VT_R8</c>), payload <c>f64</c>.</summary>
  F64 = 5,

  /// <summary>A currency amount (<c>VT_CY</c>): ten thousandths as an <c>i64</c>.</summary>
  Currency = 6,

  /// <summary>A date (<c>VT_DATE</c>): days since 1899-12-30, the time as the fraction, as an <c>f64</c>.</summary>
  Date = 7,

  /// <summary>A string (<c>VT_BSTR</c>), payload <c>str</c>.</summary>
  String = 8,

  /// <summary>A truth value (<c>VT_BOOL</c>), payload <c>bool</c>.</summary>
  Bool = 11,

  /// <summary>An unsigned 8-bit integer (<c>VT_UI1</c>), payload <c>u8</c>.</summary>
  U8 = 17,

  /// <summary>A signed 64-bit integer (<c>VT_I8</c>), payload <c>i64</c>.</summary>
  I64 = 20

}

/// <summary>
/// A value of the dynamic type made from a tag and, for every tag but <see cref="IrVariantTag.Empty"/>
/// and <see cref="IrVariantTag.Null"/>, a payload of the tag's type (<see cref="IrVariants.PayloadType"/>).
/// </summary>
/// <param name="Tag">The tag.</param>
/// <param name="Value">The payload, of the tag's payload type; <see langword="null"/> exactly for a tag without one.</param>
public sealed record IrVariantOf(IrVariantTag Tag, IrExpr? Value) : IrExpr(IrType.Variant);

/// <summary>The tag of a value of the dynamic type, as the <c>i32</c> of its <see cref="IrVariantTag"/>.</summary>
/// <param name="Value">The value, of the type <see cref="IrType.Variant"/>, evaluated once.</param>
public sealed record IrVariantTagOf(IrExpr Value) : IrExpr(IrType.I32);

/// <summary>
/// The payload of a value of the dynamic type that holds one of a tag. It traps with
/// <see cref="IrTrap.InvalidCast"/> when the value has another tag, as a cast of a .NET <c>object</c>
/// to a value type it does not hold throws.
/// </summary>
/// <param name="Value">The value, of the type <see cref="IrType.Variant"/>, evaluated once.</param>
/// <param name="Tag">The tag; one with a payload.</param>
public sealed record IrVariantValue(IrExpr Value, IrVariantTag Tag) : IrExpr(IrVariants.PayloadType(Tag) ?? IrType.Void) {

  /// <summary>
  /// Whether the frontend has tested the tag first, so that the value holds a payload of the tag
  /// wherever this node is evaluated. A backend may then read the payload without the test; the
  /// meaning is the same, because the trap cannot happen.
  /// </summary>
  public bool IsTested { get; init; }

}

/// <summary>A value of the dynamic type as it crosses the boundary of <see cref="IrInterpreter"/>: the tag and the payload as a .NET value of its type.</summary>
/// <param name="Tag">The tag.</param>
/// <param name="Value">The payload: a <see cref="short"/>, an <see cref="int"/>, a <see cref="string"/>, …; <see langword="null"/> for <see cref="IrVariantTag.Empty"/> and <see cref="IrVariantTag.Null"/>.</param>
public sealed record IrVariant(IrVariantTag Tag, object? Value) {

  /// <summary>The value that holds nothing yet.</summary>
  public static IrVariant Empty { get; } = new(IrVariantTag.Empty, null);

  /// <summary>The value known to be missing.</summary>
  public static IrVariant Null { get; } = new(IrVariantTag.Null, null);

  /// <summary>The tag and the payload, as <c>I16 5</c>, <c>Empty</c>.</summary>
  public override string ToString() => this.Value is null ? this.Tag.ToString() : $"{this.Tag} {Convert.ToString(this.Value, System.Globalization.CultureInfo.InvariantCulture)}";

}

/// <summary>
/// The rules of the dynamic type <see cref="IrType.Variant"/>. A value of it is a tag and a payload:
/// <see cref="IrVariantOf"/> makes one, <see cref="IrVariantTagOf"/> reads the tag and
/// <see cref="IrVariantValue"/> the payload of a tag, which traps on another one. Nothing else
/// operates on a variant: no operator, no comparison and no conversion. What a language does with
/// its dynamic values - Visual Basic's promotion of <c>Integer</c> arithmetic to <c>Long</c>, its
/// <c>Null</c> propagation - is the frontend's code, written over these three nodes, so every backend
/// that has them computes what the source language computes. A variant is a scalar: a local, a
/// parameter, a result or a global; no array, field or collection holds one.
/// </summary>
public static class IrVariants {

  private static readonly Dictionary<IrVariantTag, IrType> _payloads = new() {
    [IrVariantTag.I16] = IrType.I16,
    [IrVariantTag.I32] = IrType.I32,
    [IrVariantTag.F32] = IrType.F32,
    [IrVariantTag.F64] = IrType.F64,
    [IrVariantTag.Currency] = IrType.I64,
    [IrVariantTag.Date] = IrType.F64,
    [IrVariantTag.String] = IrType.String,
    [IrVariantTag.Bool] = IrType.Bool,
    [IrVariantTag.U8] = IrType.U8,
    [IrVariantTag.I64] = IrType.I64
  };

  /// <summary>Every tag, in the order of its number.</summary>
  public static IReadOnlyList<IrVariantTag> Tags { get; } = [.. Enum.GetValues<IrVariantTag>().Order()];

  /// <summary>The type of the payload of a tag, or <see langword="null"/> for <see cref="IrVariantTag.Empty"/> and <see cref="IrVariantTag.Null"/>, which have none.</summary>
  /// <param name="tag">The tag.</param>
  /// <returns>The type.</returns>
  public static IrType? PayloadType(IrVariantTag tag) => _payloads.GetValueOrDefault(tag);

  /// <summary>The tag a value of a type is boxed with when its type is all that is known, or <see langword="null"/> for a type no tag holds.</summary>
  /// <param name="type">The type.</param>
  /// <returns>The tag; <see cref="IrVariantTag.I64"/> for <c>i64</c>, never <see cref="IrVariantTag.Currency"/>, and <see cref="IrVariantTag.F64"/>, never <see cref="IrVariantTag.Date"/>.</returns>
  public static IrVariantTag? TagOf(IrType type) {
    ArgumentNullException.ThrowIfNull(type);
    return type.IsEnum ? null : type.Kind switch {
      IrTypeKind.I16 => IrVariantTag.I16,
      IrTypeKind.I32 => IrVariantTag.I32,
      IrTypeKind.F32 => IrVariantTag.F32,
      IrTypeKind.F64 => IrVariantTag.F64,
      IrTypeKind.String => IrVariantTag.String,
      IrTypeKind.Bool => IrVariantTag.Bool,
      IrTypeKind.U8 => IrVariantTag.U8,
      IrTypeKind.I64 => IrVariantTag.I64,
      _ => null
    };
  }

  /// <summary>Whether a module uses the dynamic type anywhere: in a signature, a local, a global or an expression.</summary>
  /// <param name="module">The module.</param>
  /// <returns><see langword="true"/> when a backend must be able to write it.</returns>
  public static bool Uses(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    return module.Globals.Any(global => global.Type.IsVariant)
      || module.Functions.Any(function => function.ReturnType.IsVariant || function.Parameters.Concat(function.Locals).Any(local => local.Type.IsVariant)
        || IrTree.Nodes(function.Body).Any(node => node.Type.IsVariant || node is IrVariantTagOf));
  }

}
