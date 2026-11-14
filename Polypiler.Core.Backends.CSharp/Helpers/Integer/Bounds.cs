namespace Polypiler.Core.Backends.CSharp;

internal static partial class CSharpIntegerHelpers {

  /// <summary>A signed value within two bounds; crossed bounds are the invalid-operation trap.</summary>
  public static CSharpHelper ClampSigned { get; } = Clamp("ClampSigned", "long");

  /// <summary>An unsigned value within two bounds; crossed bounds are the invalid-operation trap.</summary>
  public static CSharpHelper ClampUnsigned { get; } = Clamp("ClampUnsigned", "ulong");

  /// <summary>The magnitude of a signed value modulo 2^64; the cast of the row cuts it to the width, where the smallest value is its own.</summary>
  public static CSharpHelper AbsWrap { get; } = new("AbsWrap", "long", "long value", [
    "return value < 0L ? unchecked(-value) : value;"
  ]);

  private static CSharpHelper Clamp(string name, string type) => new(name, type, $"{type} value, {type} low, {type} high", [
    "if (low > high) {",
    "  " + Invalid,
    "}",
    "return value < low ? low : value > high ? high : value;"
  ]);

}
