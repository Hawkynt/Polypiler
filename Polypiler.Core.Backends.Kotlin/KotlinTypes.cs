using System;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Kotlin;

/// <summary>The Kotlin types of the IR's scalar types and of the arrays of them.</summary>
internal static class KotlinTypes {

  /// <summary>The Kotlin type of a scalar: <c>Byte</c> … <c>ULong</c>, <c>Float</c>, <c>Double</c>, <c>Boolean</c>; <see langword="null"/> for any other type.</summary>
  public static string? Scalar(IrType type) => type.Integral.Kind switch {
    IrTypeKind.Bool => "Boolean",
    IrTypeKind.I8 => "Byte",
    IrTypeKind.U8 => "UByte",
    IrTypeKind.I16 => "Short",
    IrTypeKind.U16 => "UShort",
    IrTypeKind.I32 => "Int",
    IrTypeKind.U32 => "UInt",
    IrTypeKind.I64 => "Long",
    IrTypeKind.U64 => "ULong",
    IrTypeKind.F32 => "Float",
    IrTypeKind.F64 => "Double",
    _ => null
  };

  /// <summary>
  /// The signed type an integer is held in where Kotlin needs a signed one - the elements of an array:
  /// the type itself for a signed integer, the signed type of the width for an unsigned one.
  /// </summary>
  public static IrType Carrier(IrType type) => type.Integral.Kind switch {
    IrTypeKind.U8 => IrType.I8,
    IrTypeKind.U16 => IrType.I16,
    IrTypeKind.U32 => IrType.I32,
    IrTypeKind.U64 => IrType.I64,
    _ => type.Integral
  };

  /// <summary>Whether an integer type is held in an array as the signed type of its width.</summary>
  public static bool IsCarried(IrType type) => type.Integral.IsInteger && !type.Integral.IsSigned;

  /// <summary>The primitive array of an element type: <c>ByteArray</c> … <c>DoubleArray</c>, <c>BooleanArray</c>; <see langword="null"/> for none.</summary>
  public static string? PrimitiveArray(IrType element) => Scalar(Carrier(element)) is { } scalar ? scalar + "Array" : null;

  /// <summary>The factory of a primitive array of the given elements: <c>intArrayOf</c> and its siblings.</summary>
  public static string ArrayOf(string primitiveArray) => char.ToLowerInvariant(primitiveArray[0]) + primitiveArray[1..] + "Of";

  /// <summary>Whether an integer type is computed in <c>Int</c> or <c>UInt</c> by Kotlin's operators: 8 and 16 bits.</summary>
  public static bool IsNarrow(IrType type) => type.Integral.IsInteger && type.Integral.Bits < 32;

  /// <summary>The type Kotlin's arithmetic gives an integer of a type: <c>Int</c> or <c>UInt</c> for 8 and 16 bits, the type itself otherwise.</summary>
  public static IrType Promoted(IrType type) {
    var integral = type.Integral;
    return !IsNarrow(integral) ? integral : integral.IsSigned ? IrType.I32 : IrType.U32;
  }

  /// <summary>The conversion method to a scalar type, <c>toInt</c> and its siblings.</summary>
  public static string To(IrType type) => "to" + (Scalar(type) ?? throw new ArgumentException($"'{type}' is no scalar of Kotlin.", nameof(type)));

  /// <summary>The suffix of an integer literal of a type of 32 or 64 bits: none, <c>L</c>, <c>u</c>, <c>uL</c>.</summary>
  public static string Suffix(IrType type) => type.Integral.Kind switch {
    IrTypeKind.I64 => "L",
    IrTypeKind.U32 => "u",
    IrTypeKind.U64 => "uL",
    _ => ""
  };

}
