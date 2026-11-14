using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>The typing rules of the operators, shared by everything that builds or checks IR.</summary>
public static class IrOperators {

  /// <summary>Whether a unary operator is defined on an operand type.</summary>
  /// <param name="op">The operator.</param>
  /// <param name="operand">The type of the operand.</param>
  /// <returns><see langword="true"/> when the operator applies.</returns>
  public static bool Accepts(IrUnaryOp op, IrType operand) => op switch {
    IrUnaryOp.Negate => operand.IsFloat || operand == IrType.I32 || operand == IrType.I64,
    IrUnaryOp.NegateChecked => operand == IrType.I32 || operand == IrType.I64,
    IrUnaryOp.BitNot => operand.IsWideInteger,
    _ => operand == IrType.Bool
  };

  /// <summary>Whether a binary operator is defined on a pair of operand types.</summary>
  /// <param name="op">The operator.</param>
  /// <param name="left">The type of the left operand.</param>
  /// <param name="right">The type of the right operand.</param>
  /// <returns><see langword="true"/> when the operator applies.</returns>
  public static bool Accepts(IrBinaryOp op, IrType left, IrType right) => op switch {
    IrBinaryOp.LogicalAnd or IrBinaryOp.LogicalOr => left == IrType.Bool && right == IrType.Bool,
    IrBinaryOp.Equal or IrBinaryOp.NotEqual => (left.IsArithmetic || left == IrType.Bool) && right == left,
    IrBinaryOp.ShiftLeft or IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical => left.IsWideInteger && right == IrType.I32,
    IrBinaryOp.AddChecked or IrBinaryOp.SubtractChecked or IrBinaryOp.MultiplyChecked
      or IrBinaryOp.And or IrBinaryOp.Or or IrBinaryOp.Xor => left.IsWideInteger && right == left,
    _ => left.IsArithmetic && right == left
  };

  /// <summary>Whether an intrinsic operation is defined on its argument types with a given result type.</summary>
  /// <param name="op">The operation.</param>
  /// <param name="result">The type of the node.</param>
  /// <param name="arguments">The types of the arguments, in order.</param>
  /// <returns><see langword="true"/> when the operation applies.</returns>
  public static bool Accepts(IrIntrinsicOp op, IrType result, IReadOnlyList<IrType> arguments) {
    // The integer and bit operations, also those on integers that are defined on floating point as well.
    if (IntegerOperations.Accepts(op, result, arguments) is { } integer)
      return integer;
    if (FloatOperationTable.Accepts(op, result, arguments) is { } floating)
      return floating;
    if (StringOperations.Accepts(op, result, arguments) is { } text)
      return text;

    bool Are(params Func<IrType, bool>[] rules) => arguments.Count == rules.Length && !rules.Where((rule, index) => !rule(arguments[index])).Any();
    bool Table(IrType type) => type.IsArray && type.IsStorable;
    // Copying, filling and clearing work on elements of a number: an element that is a struct is a value that each would have to copy.
    bool Numbers(IrType type) => Table(type) && type.Element!.IsNumeric;
    var first = arguments.Count > 0 ? arguments[0] : IrType.Void;
    bool Same(IrType type) => type == first;
    bool Float(IrType type) => type.IsFloat;
    return op switch {
      IrIntrinsicOp.ArrayOf => Table(result) && arguments.All(argument => argument == result.Element),
      IrIntrinsicOp.ArrayCopy => result == IrType.Void && Are(Numbers, type => type.IsWord, Same, type => type.IsWord, type => type.IsWord),
      IrIntrinsicOp.ArrayFill => result == IrType.Void && Are(Numbers, type => type == first.Element),
      IrIntrinsicOp.ArrayClear => result == IrType.Void && Are(Numbers),
      IrIntrinsicOp.Abs => result == first && Are(Float),
      IrIntrinsicOp.Min or IrIntrinsicOp.Max => result == first && Are(Float, Same),
      IrIntrinsicOp.Sign => result == IrType.I32 && Are(Float),
      IrIntrinsicOp.FloatBits => Are(type => type.IsFloat) && result == (first == IrType.F64 ? IrType.I64 : IrType.I32),
      IrIntrinsicOp.BitsFloat => Are(type => type == IrType.I32 || type == IrType.I64) && result == (first == IrType.I64 ? IrType.F64 : IrType.F32),
      IrIntrinsicOp.ToInt32 => result == IrType.I32 && Are(type => type == IrType.F64),
      IrIntrinsicOp.ToUint32 => result == IrType.U32 && Are(type => type == IrType.F64),
      _ => result == first && Are(type => type.IsFloat)
    };
  }

  /// <summary>Whether an operator traps instead of wrapping around when its result does not fit.</summary>
  /// <param name="op">The operator.</param>
  /// <returns><see langword="true"/> for the checked forms of addition, subtraction and multiplication.</returns>
  public static bool Traps(IrBinaryOp op)
    => op is IrBinaryOp.AddChecked or IrBinaryOp.SubtractChecked or IrBinaryOp.MultiplyChecked;

  /// <summary>
  /// Reduces a number to the value an integer type holds for it: the number modulo 2^width. A value of
  /// <c>u64</c> is carried as the 64 bits it consists of, so it is unchanged like one of <c>i64</c>.
  /// </summary>
  /// <param name="type">An integer type.</param>
  /// <param name="value">Any number.</param>
  /// <returns>The value within the range of <paramref name="type"/>.</returns>
  public static long Wrap(IrType type, long value) => type.Kind switch {
    IrTypeKind.I8 => (sbyte)value,
    IrTypeKind.U8 => (byte)value,
    IrTypeKind.I16 => (short)value,
    IrTypeKind.U16 => (ushort)value,
    IrTypeKind.I32 => (int)value,
    IrTypeKind.U32 => (uint)value,
    _ => value
  };

  /// <summary>The smallest value of an integer type.</summary>
  /// <param name="type">An integer type.</param>
  /// <returns>The minimum, 0 for an unsigned type.</returns>
  public static Int128 Minimum(IrType type) => type.IsSigned ? -(Int128.One << (type.Bits - 1)) : Int128.Zero;

  /// <summary>The largest value of an integer type.</summary>
  /// <param name="type">An integer type.</param>
  /// <returns>The maximum.</returns>
  public static Int128 Maximum(IrType type) => (Int128.One << (type.IsSigned ? type.Bits - 1 : type.Bits)) - 1;

  /// <summary>The number a carried integer stands for: the 64 bits of a <c>u64</c> read as unsigned.</summary>
  /// <param name="type">The integer type of the value.</param>
  /// <param name="value">The value as the IR carries it.</param>
  /// <returns>The mathematical value.</returns>
  public static Int128 Number(IrType type, long value) => type == IrType.U64 ? (ulong)value : value;

  /// <summary>A floating-point number as a constant of its type carries it: its IEEE 754 bits.</summary>
  /// <param name="type"><see cref="IrType.F32"/> or <see cref="IrType.F64"/>.</param>
  /// <param name="value">The number; for <c>f32</c> it is rounded to binary32 first.</param>
  /// <returns>The value of <see cref="IrConst.Value"/> for the number.</returns>
  public static long Bits(IrType type, double value)
    => type == IrType.F32 ? BitConverter.SingleToInt32Bits((float)value) : BitConverter.DoubleToInt64Bits(value);

  /// <summary>
  /// ECMA-262's <c>ToInt32</c> and <c>ToUint32</c>: the number without its fraction, modulo 2^32, as
  /// a value of the given 32-bit type. The remainder of two doubles is exact, so the result is the
  /// one the standard defines for every number, however large; a NaN and the infinities give 0.
  /// </summary>
  /// <param name="type"><see cref="IrType.I32"/> or <see cref="IrType.U32"/>.</param>
  /// <param name="value">Any number.</param>
  /// <returns>The value as the IR carries an integer of <paramref name="type"/>.</returns>
  public static long ToWord(IrType type, double value)
    => double.IsFinite(value) ? Wrap(type, (long)(Math.Truncate(value) % 4294967296.0)) : 0;

  /// <summary>
  /// ECMA-262's <c>Math.round</c>: the nearest integral number, a tie going towards positive
  /// infinity, with the sign of the operand kept on a zero. Below 2^52 the distance to the next lower
  /// integral number is exact; from there on every double is integral and is its own result.
  /// </summary>
  /// <param name="value">Any number.</param>
  /// <returns>The rounded number.</returns>
  public static double RoundHalfUp(double value) {
    var floor = Math.Floor(value);
    var rounded = value - floor >= 0.5 ? floor + 1 : floor;
    return rounded == 0 ? Math.CopySign(0.0, value) : rounded;
  }

  /// <summary>The floating-point number a constant carries.</summary>
  /// <param name="type"><see cref="IrType.F32"/> or <see cref="IrType.F64"/>.</param>
  /// <param name="bits">The value of <see cref="IrConst.Value"/>.</param>
  /// <returns>The number; a binary32 one widened exactly.</returns>
  public static double Float(IrType type, long bits)
    => type == IrType.F32 ? BitConverter.Int32BitsToSingle((int)bits) : BitConverter.Int64BitsToDouble(bits);

  /// <summary>Whether every value of one integer type is also a value of another.</summary>
  /// <param name="type">The type that is to hold the values.</param>
  /// <param name="other">The type the values come from.</param>
  /// <returns><see langword="true"/> when a conversion from <paramref name="other"/> loses nothing.</returns>
  public static bool Contains(IrType type, IrType other)
    => Minimum(type) <= Minimum(other) && Maximum(type) >= Maximum(other);

}
