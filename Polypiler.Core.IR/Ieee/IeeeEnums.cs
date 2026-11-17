using System;

namespace Polypiler.Core.IR;

/// <summary>
/// The rounding of an operation whose exact result is not a value of its format (IEEE 754-2019 §4.3).
/// The value is what the <c>i32</c> constant that carries the mode on an operation holds.
/// </summary>
public enum RoundingMode {

  /// <summary><c>rne</c>: to the nearest value, a tie to the one with an even significand.</summary>
  NearestEven = 0,

  /// <summary><c>rtz</c>: to the nearest value not larger in magnitude.</summary>
  TowardZero = 1,

  /// <summary><c>rup</c>: to the nearest value not below the exact result.</summary>
  Up = 2,

  /// <summary><c>rdn</c>: to the nearest value not above the exact result.</summary>
  Down = 3,

  /// <summary><c>rna</c>: to the nearest value, a tie to the one larger in magnitude.</summary>
  NearestAway = 4

}

/// <summary>
/// The fourteen comparisons of two floating-point numbers (IEEE 754-2019 §5.11): an ordered one is
/// false and an unordered one true when an operand is a NaN; <c>-0</c> equals <c>+0</c>. The value is
/// what the <c>i32</c> constant that carries the predicate holds.
/// </summary>
public enum FloatPredicate {

  /// <summary><c>oeq</c>: ordered and equal.</summary>
  OrderedEqual = 0,

  /// <summary><c>one</c>: ordered and not equal.</summary>
  OrderedNotEqual = 1,

  /// <summary><c>olt</c>: ordered and less.</summary>
  OrderedLess = 2,

  /// <summary><c>ole</c>: ordered and less or equal.</summary>
  OrderedLessOrEqual = 3,

  /// <summary><c>ogt</c>: ordered and greater.</summary>
  OrderedGreater = 4,

  /// <summary><c>oge</c>: ordered and greater or equal.</summary>
  OrderedGreaterOrEqual = 5,

  /// <summary><c>ord</c>: neither operand is a NaN.</summary>
  Ordered = 6,

  /// <summary><c>ueq</c>: unordered or equal.</summary>
  UnorderedEqual = 7,

  /// <summary><c>une</c>: unordered or not equal.</summary>
  UnorderedNotEqual = 8,

  /// <summary><c>ult</c>: unordered or less.</summary>
  UnorderedLess = 9,

  /// <summary><c>ule</c>: unordered or less or equal.</summary>
  UnorderedLessOrEqual = 10,

  /// <summary><c>ugt</c>: unordered or greater.</summary>
  UnorderedGreater = 11,

  /// <summary><c>uge</c>: unordered or greater or equal.</summary>
  UnorderedGreaterOrEqual = 12,

  /// <summary><c>uno</c>: an operand is a NaN.</summary>
  Unordered = 13

}

/// <summary>The class of a floating-point number as <see cref="IrIntrinsicOp.FloatClassify"/> gives it: a set of bits of a <c>u8</c>.</summary>
[Flags]
public enum FloatClass {

  /// <summary>No bit.</summary>
  None = 0,

  /// <summary>A zero of either sign.</summary>
  Zero = 1,

  /// <summary>A subnormal number.</summary>
  Subnormal = 2,

  /// <summary>A normal number.</summary>
  Normal = 4,

  /// <summary>An infinity.</summary>
  Infinite = 8,

  /// <summary>A quiet NaN.</summary>
  QuietNaN = 16,

  /// <summary>A signalling NaN.</summary>
  SignalingNaN = 32,

  /// <summary>The sign bit is set, of any class.</summary>
  Negative = 64

}

/// <summary>The short names of the rounding modes and the predicates as the reference page and the issues spell them.</summary>
public static class IeeeNames {

  private static readonly string[] _modes = ["rne", "rtz", "rup", "rdn", "rna"];

  private static readonly string[] _predicates = ["oeq", "one", "olt", "ole", "ogt", "oge", "ord", "ueq", "une", "ult", "ule", "ugt", "uge", "uno"];

  /// <summary>The short name of a rounding mode: <c>rne</c>, <c>rtz</c>, <c>rup</c>, <c>rdn</c> or <c>rna</c>.</summary>
  /// <param name="mode">The mode.</param>
  /// <returns>The name.</returns>
  /// <exception cref="ArgumentOutOfRangeException">The value is no mode.</exception>
  public static string Of(RoundingMode mode)
    => (int)mode is >= 0 and < 5 ? _modes[(int)mode] : throw new ArgumentOutOfRangeException(nameof(mode), mode, "no rounding mode");

  /// <summary>The short name of a predicate, <c>oeq</c> to <c>uno</c>.</summary>
  /// <param name="predicate">The predicate.</param>
  /// <returns>The name.</returns>
  /// <exception cref="ArgumentOutOfRangeException">The value is no predicate.</exception>
  public static string Of(FloatPredicate predicate)
    => (int)predicate is >= 0 and < 14 ? _predicates[(int)predicate] : throw new ArgumentOutOfRangeException(nameof(predicate), predicate, "no predicate");

  /// <summary>The rounding mode of a short name.</summary>
  /// <param name="name">The name.</param>
  /// <param name="mode">The mode.</param>
  /// <returns><see langword="true"/> for one of the five names.</returns>
  public static bool TryParse(string name, out RoundingMode mode) {
    var index = Array.IndexOf(_modes, name);
    mode = (RoundingMode)Math.Max(index, 0);
    return index >= 0;
  }

  /// <summary>The predicate of a short name.</summary>
  /// <param name="name">The name.</param>
  /// <param name="predicate">The predicate.</param>
  /// <returns><see langword="true"/> for one of the fourteen names.</returns>
  public static bool TryParse(string name, out FloatPredicate predicate) {
    var index = Array.IndexOf(_predicates, name);
    predicate = (FloatPredicate)Math.Max(index, 0);
    return index >= 0;
  }

}
