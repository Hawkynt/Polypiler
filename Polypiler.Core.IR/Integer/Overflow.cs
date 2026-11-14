using System.Collections.Generic;
using System.Numerics;

namespace Polypiler.Core.IR;

public static partial class IntegerOperations {

  /// <summary>Whether a sum, a difference or a product leaves the type, of every integer type; none of them traps.</summary>
  private static IEnumerable<IntegerOperation> Overflow => [
    Fixed(IrIntrinsicOp.AddOverflows, IrType.Bool, _integers, type => [type, type], (_, types, arguments) => AddOverflows(types[0], Long(arguments, 0), Long(arguments, 1))),
    Fixed(IrIntrinsicOp.SubOverflows, IrType.Bool, _integers, type => [type, type], (_, types, arguments) => SubOverflows(types[0], Long(arguments, 0), Long(arguments, 1))),
    Fixed(IrIntrinsicOp.MulOverflows, IrType.Bool, _integers, type => [type, type], (_, types, arguments) => MulOverflows(types[0], Long(arguments, 0), Long(arguments, 1)))
  ];

  /// <summary>Whether the exact sum is not a value of the type.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="left">The first value.</param>
  /// <param name="right">The second value.</param>
  /// <returns><see langword="true"/> when <see cref="IrBinaryOp.AddChecked"/> would trap.</returns>
  public static bool AddOverflows(IrType type, long left, long right) => Outside(type, Number(type, left) + Number(type, right));

  /// <summary>Whether the exact difference is not a value of the type.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="left">The minuend.</param>
  /// <param name="right">The subtrahend.</param>
  /// <returns><see langword="true"/> when <see cref="IrBinaryOp.SubtractChecked"/> would trap.</returns>
  public static bool SubOverflows(IrType type, long left, long right) => Outside(type, Number(type, left) - Number(type, right));

  /// <summary>Whether the exact product is not a value of the type.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="left">The first factor.</param>
  /// <param name="right">The second factor.</param>
  /// <returns><see langword="true"/> when <see cref="IrBinaryOp.MultiplyChecked"/> would trap.</returns>
  public static bool MulOverflows(IrType type, long left, long right) {
    var product = (BigInteger)Number(type, left) * (BigInteger)Number(type, right);
    return product < (BigInteger)IrOperators.Minimum(type) || product > (BigInteger)IrOperators.Maximum(type);
  }

  private static bool Outside(IrType type, System.Int128 number) => number < IrOperators.Minimum(type) || number > IrOperators.Maximum(type);

}
