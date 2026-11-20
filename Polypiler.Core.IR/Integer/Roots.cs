using System.Collections.Generic;

namespace Polypiler.Core.IR;

public static partial class IntegerOperations {

  /// <summary>The integer square root of every unsigned type (#198).</summary>
  private static IEnumerable<IntegerOperation> Roots => [
    Same(IrIntrinsicOp.ISqrt, _unsigned, type => [type], (_, types, arguments) => ISqrt(types[0], Long(arguments, 0)))
  ];

  /// <summary>
  /// The integer square root: the largest <c>r</c> with <c>r * r</c> at most the value, which is at most
  /// <c>2^(width/2) - 1</c>; 0 for 0.
  /// </summary>
  /// <param name="type">An unsigned integer type.</param>
  /// <param name="value">The value, as it is carried.</param>
  /// <returns>The root.</returns>
  public static long ISqrt(IrType type, long value) {
    var pattern = Pattern(type, value);
    var root = (ulong)System.Math.Sqrt(pattern);
    // The double rounds a value above 2^53; the root is put right from either side.
    while (root > uint.MaxValue || root * root > pattern)
      --root;
    while (root < uint.MaxValue && (root + 1) * (root + 1) <= pattern)
      ++root;
    return Value(type, root);
  }

}
