namespace Polypiler.Core.Backends.CSharp;

internal static partial class CSharpIntegerHelpers {

  /// <summary>Whether an unsigned value has exactly one bit set.</summary>
  public static CSharpHelper IsPow2Unsigned { get; } = new("IsPow2Unsigned", "bool", "ulong value", [
    "return value != 0UL && (value & (value - 1UL)) == 0UL;"
  ]);

  /// <summary>Whether a signed value is positive and has exactly one bit set.</summary>
  public static CSharpHelper IsPow2Signed { get; } = new("IsPow2Signed", "bool", "long value", [
    "return value > 0L && (value & (value - 1L)) == 0L;"
  ]);

  /// <summary>
  /// The smallest power of two not below an unsigned pattern, 0 for 0 and where the doubling leaves
  /// the 64 bits; the cast to the type of the row cuts a power of 2^width to the 0 it wraps to.
  /// </summary>
  public static CSharpHelper RoundUpPow2 { get; } = new("RoundUpPow2", "ulong", "ulong value", [
    "if (value == 0UL) {",
    "  return 0UL;",
    "}",
    "ulong power = 1UL;",
    "while (power != 0UL && power < value) {",
    "  power <<= 1;",
    "}",
    "return power;"
  ]);

}
