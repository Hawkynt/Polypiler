// SPDX-License-Identifier: MIT
namespace Polypiler.Core.Backends.CSharp;

/// <summary>
/// The helpers of the floating-point operations that trap, which the portable implementations cannot
/// express: <c>Math.Clamp</c> before the frameworks that have it, and the rounding to digits of
/// <c>Math.Round(double, int, MidpointRounding)</c> and <c>MathF.Round(float, int, MidpointRounding)</c>
/// as .NET 10 computes it (MIT), with the mode given as the <c>int</c> of <c>MidpointRounding</c>. They
/// throw the exceptions .NET throws. Written in C# 2 over the base library of .NET Framework 2.0.
/// </summary>
internal static class CSharpFloatHelpers {

  /// <summary><c>Math.Clamp(double, double, double)</c>.</summary>
  public static CSharpHelper ClampDouble { get; } = Clamp("double");

  /// <summary><c>Math.Clamp(float, float, float)</c>.</summary>
  public static CSharpHelper ClampSingle { get; } = Clamp("float");

  /// <summary>The rounding of a <c>double</c> to 0 to 15 digits; from 10^16 on the number is returned.</summary>
  public static CSharpHelper RoundDigitsDouble { get; } = new("RoundDigits", "double", "double value, int digits, int mode", [
    "if (digits < 0 || digits > 15) {",
    "  throw new global::System.ArgumentOutOfRangeException(\"digits\");",
    "}",
    "if (global::System.Math.Abs(value) < 1e16) {",
    "  double power10 = 1.0;",
    "  for (int index = 0; index < digits; ++index) {",
    "    power10 *= 10.0;",
    "  }",
    "  double scaled = value * power10;",
    "  double rounded = mode == 1 ? global::System.Math.Round(scaled, global::System.MidpointRounding.AwayFromZero)",
    "    : mode == 2 ? global::System.Math.Truncate(scaled)",
    "    : mode == 3 ? global::System.Math.Floor(scaled)",
    "    : mode == 4 ? global::System.Math.Ceiling(scaled)",
    "    : global::System.Math.Round(scaled);",
    "  value = rounded / power10;",
    "}",
    "return value;"
  ]);

  /// <summary>
  /// The rounding of a <c>float</c> to 0 to 6 digits; from 10^8 on the number is returned. Every step
  /// is cast to <c>float</c>; rounding a <c>float</c> to an integer through <c>double</c> is exact.
  /// </summary>
  public static CSharpHelper RoundDigitsSingle { get; } = new("RoundDigits", "float", "float value, int digits, int mode", [
    "if (digits < 0 || digits > 6) {",
    "  throw new global::System.ArgumentOutOfRangeException(\"digits\");",
    "}",
    "if (global::System.Math.Abs(value) < 1e8f) {",
    "  float power10 = 1.0f;",
    "  for (int index = 0; index < digits; ++index) {",
    "    power10 = (float)(power10 * 10.0f);",
    "  }",
    "  double scaled = (float)(value * power10);",
    "  float rounded = (float)(mode == 1 ? global::System.Math.Round(scaled, global::System.MidpointRounding.AwayFromZero)",
    "    : mode == 2 ? global::System.Math.Truncate(scaled)",
    "    : mode == 3 ? global::System.Math.Floor(scaled)",
    "    : mode == 4 ? global::System.Math.Ceiling(scaled)",
    "    : global::System.Math.Round(scaled));",
    "  value = (float)(rounded / power10);",
    "}",
    "return value;"
  ]);

  private static CSharpHelper Clamp(string type) => new("Clamp", type, $"{type} value, {type} min, {type} max", [
    "if (min > max) {",
    "  throw new global::System.ArgumentException(\"min is greater than max\");",
    "}",
    "return value < min ? min : value > max ? max : value;"
  ]);

}
