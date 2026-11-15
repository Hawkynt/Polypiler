// SPDX-License-Identifier: MIT
namespace Polypiler.Core.Backends.CSharp;

/// <summary>
/// The helpers the capability table names. The bit counts follow Warren, <i>Hacker's Delight</i>,
/// 2nd edition: the population count of §5-1, the binary searches for the leading zeros of §5-3 and
/// the trailing zeros of §5-4, and the 64-bit product in four partial products of §8-2. The smaller
/// and the larger of two floating-point numbers are the <c>minimum</c> and <c>maximum</c> of
/// IEEE 754-2019, which order -0 before +0 and return a NaN when either operand is one.
/// </summary>
internal static class CSharpHelpers {

  private const string Base = "global::System.";

  /// <summary>
  /// A floating-point number as an <c>int</c> within two bounds: the nearest one when it lies outside,
  /// 0 for a NaN. The comparisons come first, so the cast only ever sees a number within the range.
  /// </summary>
  public static CSharpHelper Saturated { get; } = new("Saturated", "int", "double value, int low, int high", [
    "return value >= high ? high : value > low ? (int)value : value <= low ? low : 0;"
  ]);

  /// <summary>A floating-point number as the nearest <c>uint</c>, 0 for a NaN.</summary>
  public static CSharpHelper SaturatedUInt32 { get; } = new("SaturatedUInt32", "uint", "double value", [
    "return value >= 4294967295.0 ? 4294967295u : value > 0.0 ? (uint)value : 0u;"
  ]);

  /// <summary>A floating-point number as the nearest <c>long</c>, 0 for a NaN. The upper bound is 2^63, which no <c>long</c> reaches.</summary>
  public static CSharpHelper SaturatedInt64 { get; } = new("SaturatedInt64", "long", "double value", [
    "return value >= 9223372036854775808.0 ? 9223372036854775807L : value > -9223372036854775808.0 ? (long)value : value <= -9223372036854775808.0 ? -9223372036854775808L : 0L;"
  ]);

  /// <summary>A floating-point number as the nearest <c>ulong</c>, 0 for a NaN. The upper bound is 2^64.</summary>
  public static CSharpHelper SaturatedUInt64 { get; } = new("SaturatedUInt64", "ulong", "double value", [
    "return value >= 18446744073709551616.0 ? 18446744073709551615UL : value > 0.0 ? (ulong)value : 0UL;"
  ]);

  /// <summary>
  /// ECMA-262's <c>ToInt32</c>: the number without its fraction modulo 2^32, a NaN and the infinities 0.
  /// A cast of the number itself saturates; the remainder of two doubles is exact and lies within
  /// 2^32, where the cast to <c>long</c> changes nothing, and the last cast keeps the low 32 bits.
  /// Only what .NET Framework 2.0 has is used, so the helper is the same in every profile.
  /// </summary>
  public static CSharpHelper ToInt32 { get; } = new("ToInt32", "int", "double value", [
    "return double.IsNaN(value) || double.IsInfinity(value) ? 0 : unchecked((int)(long)(global::System.Math.Truncate(value) % 4294967296.0));"
  ]);

  /// <summary>ECMA-262's <c>ToUint32</c>: like <see cref="ToInt32"/>, read as an unsigned integer.</summary>
  public static CSharpHelper ToUint32 { get; } = new("ToUint32", "uint", "double value", [
    "return double.IsNaN(value) || double.IsInfinity(value) ? 0u : unchecked((uint)(long)(global::System.Math.Truncate(value) % 4294967296.0));"
  ]);

  /// <summary>
  /// ECMA-262's <c>Math.round</c>: a tie goes towards positive infinity, and a zero keeps the sign of
  /// the number - told by the sign of its reciprocal, since <c>Math.CopySign</c> came late.
  /// </summary>
  public static CSharpHelper RoundHalfUp { get; } = new("RoundHalfUp", "double", "double value", [
    "double floor = global::System.Math.Floor(value);",
    "double rounded = value - floor >= 0.5 ? floor + 1 : floor;",
    "return rounded != 0 ? rounded : value < 0 || 1 / value < 0 ? -0.0 : 0.0;"
  ]);

  /// <summary>Evaluates an expression for its effects where the language has no discard.</summary>
  public static CSharpHelper Ignore { get; } = new("Ignore", "void", "T value", [], "<T>");

  /// <summary><c>Array.Fill</c> as a loop.</summary>
  public static CSharpHelper Fill { get; } = new("Fill", "void", "T[] array, T value", [
    "for (int index = 0; index < array.Length; ++index) {",
    "  array[index] = value;",
    "}"
  ], "<T>");

  /// <summary><c>Array.Clear</c> of a whole array, through the overload that takes a range.</summary>
  public static CSharpHelper Clear { get; } = new("Clear", "void", Base + "Array array", [
    Base + "Array.Clear(array, 0, array.Length);"
  ]);

  /// <summary>The smaller of two <c>float</c>.</summary>
  public static CSharpHelper MinSingle { get; } = Minimum("float", "0.0f", "1.0f");

  /// <summary>The smaller of two <c>double</c>.</summary>
  public static CSharpHelper MinDouble { get; } = Minimum("double", "0.0", "1.0");

  /// <summary>The larger of two <c>float</c>.</summary>
  public static CSharpHelper MaxSingle { get; } = Maximum("float", "0.0f", "1.0f");

  /// <summary>The larger of two <c>double</c>.</summary>
  public static CSharpHelper MaxDouble { get; } = Maximum("double", "0.0", "1.0");

  /// <summary>The bits of a <c>float</c>, through the bytes <c>BitConverter</c> has always given.</summary>
  public static CSharpHelper SingleToInt32Bits { get; } = new("SingleToInt32Bits", "int", "float value", [
    $"return {Base}BitConverter.ToInt32({Base}BitConverter.GetBytes(value), 0);"
  ]);

  /// <summary>The <c>float</c> with the bits of an <c>int</c>.</summary>
  public static CSharpHelper Int32BitsToSingle { get; } = new("Int32BitsToSingle", "float", "int value", [
    $"return {Base}BitConverter.ToSingle({Base}BitConverter.GetBytes(value), 0);"
  ]);

  /// <summary>Two equal numbers are told apart only as zeros: the reciprocal of -0 is negative.</summary>
  private static CSharpHelper Minimum(string type, string zero, string one) => new("Min", type, $"{type} left, {type} right", [
    "if (left != right) {",
    $"  return {type}.IsNaN(left) || left < right ? left : right;",
    "}",
    $"return left == {zero} && {one} / left < {zero} ? left : right;"
  ]);

  private static CSharpHelper Maximum(string type, string zero, string one) => new("Max", type, $"{type} left, {type} right", [
    "if (left != right) {",
    $"  return {type}.IsNaN(left) || left > right ? left : right;",
    "}",
    $"return right == {zero} && {one} / right < {zero} ? left : right;"
  ]);

}
