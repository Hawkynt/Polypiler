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


}
