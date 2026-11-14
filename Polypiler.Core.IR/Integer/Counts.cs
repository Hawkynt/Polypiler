using System.Collections.Generic;
using System.Numerics;

namespace Polypiler.Core.IR;

public static partial class IntegerOperations {

  /// <summary>The counts of bits, each an <c>i32</c>, of every integer type.</summary>
  private static IEnumerable<IntegerOperation> Counts => [
    Count(IrIntrinsicOp.PopCount, PopCount),
    Count(IrIntrinsicOp.LeadingZeroCount, LeadingZeroCount),
    Count(IrIntrinsicOp.TrailingZeroCount, TrailingZeroCount),
    // The base-2 logarithm of a floating-point number is the same operation, which is not this class's concern.
    Count(IrIntrinsicOp.Log2, Log2, true),
    Count(IrIntrinsicOp.Parity, Parity)
  ];

  private static IntegerOperation Count(IrIntrinsicOp op, System.Func<IrType, long, int> count, bool shared = false)
    => Fixed(op, IrType.I32, _integers, type => [type], (_, types, arguments) => (long)count(types[0], Long(arguments, 0)), shared);

  /// <summary>The number of set bits of a value; of a signed value those of its bit pattern.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="value">The value.</param>
  /// <returns>0 to the width.</returns>
  public static int PopCount(IrType type, long value) => BitOperations.PopCount(Pattern(type, value));

  /// <summary>The number of zero bits above the highest set bit; the width for 0.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="value">The value.</param>
  /// <returns>0 to the width.</returns>
  public static int LeadingZeroCount(IrType type, long value) => BitOperations.LeadingZeroCount(Pattern(type, value)) - (64 - Width(type));

  /// <summary>The number of zero bits below the lowest set bit; the width for 0.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="value">The value.</param>
  /// <returns>0 to the width.</returns>
  public static int TrailingZeroCount(IrType type, long value) {
    var pattern = Pattern(type, value);
    return pattern == 0 ? Width(type) : BitOperations.TrailingZeroCount(pattern);
  }

  /// <summary>The position of the highest set bit; 0 for 0. A negative value has its top bit set.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="value">The value.</param>
  /// <returns>0 to the width minus one.</returns>
  public static int Log2(IrType type, long value) => BitOperations.Log2(Pattern(type, value));

  /// <summary>Whether the number of set bits is odd, as 1, or even, as 0.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="value">The value.</param>
  /// <returns>0 or 1.</returns>
  public static int Parity(IrType type, long value) => PopCount(type, value) & 1;

}
