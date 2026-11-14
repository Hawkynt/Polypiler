namespace Polypiler.Core.Backends.CSharp;

internal static partial class CSharpIntegerHelpers {

  /// <summary>The lowest <c>count</c> bits set, for a count from 0 to the width.</summary>
  public static CSharpHelper LowMask { get; } = new("LowMask", "ulong", "int count, int bits", [
    "if (count < 0 || count > bits) {",
    "  " + Invalid,
    "}",
    "return count == 64 ? ulong.MaxValue : (1UL << count) - 1UL;"
  ]);

  /// <summary>The lowest set bit of a pattern alone, <c>x &amp; -x</c> (Warren, <i>Hacker's Delight</i>, 2nd ed., §2-1).</summary>
  public static CSharpHelper IsolateLowestSet { get; } = new("IsolateLowestSet", "ulong", "ulong value", [
    "return value & unchecked(~value + 1UL);"
  ]);

  /// <summary>A pattern without its lowest set bit, <c>x &amp; (x - 1)</c>.</summary>
  public static CSharpHelper ClearLowestSet { get; } = new("ClearLowestSet", "ulong", "ulong value", [
    "return value & unchecked(value - 1UL);"
  ]);

  /// <summary>The bits up to and including the lowest set bit, <c>x ^ (x - 1)</c>; the cast of the row cuts the all ones of 0 to the width.</summary>
  public static CSharpHelper MaskToLowestSet { get; } = new("MaskToLowestSet", "ulong", "ulong value", [
    "return value ^ unchecked(value - 1UL);"
  ]);

  /// <summary>The lowest <c>bits</c> bits of a pattern of <c>width</c> bits extended by the highest of them.</summary>
  public static CSharpHelper SignExtend { get; } = new("SignExtend", "long", "ulong value, int bits, int width", [
    .. Extension,
    "return unchecked((long)(value << (64 - bits))) >> (64 - bits);"
  ]);

  /// <summary>The lowest <c>bits</c> bits of a pattern of <c>width</c> bits with zeros above.</summary>
  public static CSharpHelper ZeroExtend { get; } = new("ZeroExtend", "ulong", "ulong value, int bits, int width", [
    .. Extension,
    "return bits == 64 ? value : value & ((1UL << bits) - 1UL);"
  ]);

  private static string[] Extension => [
    "if (bits < 1 || bits > width) {",
    "  " + Invalid,
    "}"
  ];

}
