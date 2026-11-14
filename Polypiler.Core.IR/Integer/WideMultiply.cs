using System.Collections.Generic;
using System.Numerics;

namespace Polypiler.Core.IR;

public static partial class IntegerOperations {

  /// <summary>
  /// The full product of two 32-bit integers, and the upper half of the double-width product of two
  /// integers of every type.
  /// </summary>
  private static IEnumerable<IntegerOperation> WideMultiply => [
    new(IrIntrinsicOp.BigMul, [(IrType.I64, [IrType.I32, IrType.I32]), (IrType.U64, [IrType.U32, IrType.U32])],
      (_, types, arguments) => BigMul(types[0], Long(arguments, 0), Long(arguments, 1)), false),
    Same(IrIntrinsicOp.MulHigh, _integers, type => [type, type], (_, types, arguments) => MulHigh(types[0], Long(arguments, 0), Long(arguments, 1)))
  ];

  /// <summary>The exact product of two <c>i32</c> as an <c>i64</c>, or of two <c>u32</c> as a <c>u64</c>.</summary>
  /// <param name="type"><see cref="IrType.I32"/> or <see cref="IrType.U32"/>.</param>
  /// <param name="left">The first factor.</param>
  /// <param name="right">The second factor.</param>
  /// <returns>The product, carried as a value of the 64-bit type.</returns>
  public static long BigMul(IrType type, long left, long right) => left * right;

  /// <summary>
  /// The upper half of the product of two values computed in twice their width: for a signed type
  /// the product of the two signed numbers, its upper half read as signed.
  /// </summary>
  /// <param name="type">An integer type.</param>
  /// <param name="left">The first factor.</param>
  /// <param name="right">The second factor.</param>
  /// <returns>The upper <c>width</c> bits of the product.</returns>
  public static long MulHigh(IrType type, long left, long right) {
    var product = (BigInteger)Number(type, left) * (BigInteger)Number(type, right);
    return Value(type, (ulong)((product >> Width(type)) & ulong.MaxValue));
  }

}
