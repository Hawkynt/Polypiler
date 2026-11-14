using System;
using System.Collections.Generic;

namespace Polypiler.Core.IR;

public static partial class IntegerOperations {

  /// <summary>
  /// Addition with a carry and subtraction with a borrow, of the unsigned types: <c>(a, b, in)</c>
  /// with the incoming carry or borrow a truth value; one operation gives the low word, its partner
  /// the outgoing carry or borrow.
  /// </summary>
  private static IEnumerable<IntegerOperation> Carry => [
    Same(IrIntrinsicOp.AddCarry, _unsigned, type => [type, type, IrType.Bool],
      (_, types, arguments) => AddCarry(types[0], Long(arguments, 0), Long(arguments, 1), (bool)arguments[2])),
    Fixed(IrIntrinsicOp.CarryOut, IrType.Bool, _unsigned, type => [type, type, IrType.Bool],
      (_, types, arguments) => CarryOut(types[0], Long(arguments, 0), Long(arguments, 1), (bool)arguments[2])),
    Same(IrIntrinsicOp.SubBorrow, _unsigned, type => [type, type, IrType.Bool],
      (_, types, arguments) => SubBorrow(types[0], Long(arguments, 0), Long(arguments, 1), (bool)arguments[2])),
    Fixed(IrIntrinsicOp.BorrowOut, IrType.Bool, _unsigned, type => [type, type, IrType.Bool],
      (_, types, arguments) => BorrowOut(types[0], Long(arguments, 0), Long(arguments, 1), (bool)arguments[2]))
  ];

  /// <summary><c>a + b + carry</c> modulo 2^width.</summary>
  /// <param name="type">An unsigned integer type.</param>
  /// <param name="left">The first value.</param>
  /// <param name="right">The second value.</param>
  /// <param name="carry">The incoming carry.</param>
  /// <returns>The low word of the sum.</returns>
  public static long AddCarry(IrType type, long left, long right, bool carry) => (long)(ulong)(Sum(type, left, right, carry) & Ones(Width(type)));

  /// <summary>Whether <c>a + b + carry</c> does not fit the type: the outgoing carry.</summary>
  /// <param name="type">An unsigned integer type.</param>
  /// <param name="left">The first value.</param>
  /// <param name="right">The second value.</param>
  /// <param name="carry">The incoming carry.</param>
  /// <returns><see langword="true"/> when the sum reaches 2^width.</returns>
  public static bool CarryOut(IrType type, long left, long right, bool carry) => Sum(type, left, right, carry) > Ones(Width(type));

  /// <summary><c>a - b - borrow</c> modulo 2^width.</summary>
  /// <param name="type">An unsigned integer type.</param>
  /// <param name="left">The minuend.</param>
  /// <param name="right">The subtrahend.</param>
  /// <param name="borrow">The incoming borrow.</param>
  /// <returns>The low word of the difference.</returns>
  public static long SubBorrow(IrType type, long left, long right, bool borrow) => (long)(ulong)(Difference(type, left, right, borrow) & Ones(Width(type)));

  /// <summary>Whether <c>a - b - borrow</c> is negative: the outgoing borrow.</summary>
  /// <param name="type">An unsigned integer type.</param>
  /// <param name="left">The minuend.</param>
  /// <param name="right">The subtrahend.</param>
  /// <param name="borrow">The incoming borrow.</param>
  /// <returns><see langword="true"/> when the difference is below zero.</returns>
  public static bool BorrowOut(IrType type, long left, long right, bool borrow) => Difference(type, left, right, borrow) < 0;

  private static Int128 Sum(IrType type, long left, long right, bool carry) => Number(type, left) + Number(type, right) + (carry ? 1 : 0);

  private static Int128 Difference(IrType type, long left, long right, bool borrow) => Number(type, left) - Number(type, right) - (borrow ? 1 : 0);

}
