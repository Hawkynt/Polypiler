// SPDX-License-Identifier: MIT
namespace Polypiler.Core.Backends.CSharp;

/// <summary>
/// The helpers the C# backend writes itself, for the forms that are no operation of the IR helper library:
/// a discard, <c>Array.Fill</c> and <c>Array.Clear</c>.
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
