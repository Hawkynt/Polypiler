namespace Polypiler.Core.Backends.CSharp;

internal static partial class CSharpIntegerHelpers {

  /// <summary>The field of <c>length</c> bits from bit <c>start</c> on of a pattern of <c>bits</c> bits, at the bottom.</summary>
  public static CSharpHelper ExtractBits { get; } = new("ExtractBits", "ulong", "ulong value, int start, int length, int bits", [
    .. Field,
    "if (length == 0) {",
    "  return 0UL;",
    "}",
    "ulong mask = length == 64 ? ulong.MaxValue : (1UL << length) - 1UL;",
    "return (value >> start) & mask;"
  ]);

  /// <summary>The pattern with that field replaced by the lowest bits of another.</summary>
  public static CSharpHelper InsertBits { get; } = new("InsertBits", "ulong", "ulong value, ulong field, int start, int length, int bits", [
    .. Field,
    "if (length == 0) {",
    "  return value;",
    "}",
    "ulong mask = (length == 64 ? ulong.MaxValue : (1UL << length) - 1UL) << start;",
    "return (value & ~mask) | ((field << start) & mask);"
  ]);

  /// <summary>Whether one bit of a pattern of <c>bits</c> bits is set.</summary>
  public static CSharpHelper TestBit { get; } = new("TestBit", "bool", "ulong value, int position, int bits", [
    .. Position,
    "return ((value >> position) & 1UL) != 0UL;"
  ]);

  /// <summary>The pattern with one bit set.</summary>
  public static CSharpHelper SetBit { get; } = new("SetBit", "ulong", "ulong value, int position, int bits", [.. Position, "return value | (1UL << position);"]);

  /// <summary>The pattern with one bit cleared.</summary>
  public static CSharpHelper ClearBit { get; } = new("ClearBit", "ulong", "ulong value, int position, int bits", [.. Position, "return value & ~(1UL << position);"]);

  /// <summary>The pattern with one bit inverted.</summary>
  public static CSharpHelper ToggleBit { get; } = new("ToggleBit", "ulong", "ulong value, int position, int bits", [.. Position, "return value ^ (1UL << position);"]);

  /// <summary>The check that a field lies within the width; the sum of start and length is taken in 64 bits, where it cannot overflow.</summary>
  private static string[] Field => [
    "if (start < 0 || length < 0 || (long)start + length > bits) {",
    "  " + Invalid,
    "}"
  ];

  private static string[] Position => [
    "if (position < 0 || position >= bits) {",
    "  " + Invalid,
    "}"
  ];

}
