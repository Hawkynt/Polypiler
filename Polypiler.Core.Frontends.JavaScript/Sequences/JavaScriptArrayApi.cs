using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>One construct of the table of JavaScript's array callbacks: what it is, what the IR makes of it, and why it is refused when it is.</summary>
/// <param name="Construct">The construct as the source writes it.</param>
/// <param name="Ir">What it becomes, empty for a refused one.</param>
/// <param name="Refusal">Why it is refused, empty for one that is imported.</param>
public sealed record JavaScriptArrayMember(string Construct, string Ir, string Refusal = "");

/// <summary>
/// The methods of JavaScript arrays that take a callback, as the frontend imports them (#1329), and
/// those it refuses, each with the reason - the table the tests check against the lowering. Every
/// imported one is a loop of the IR over the array, eager as JavaScript is.
/// </summary>
public static class JavaScriptArrayApi {

  /// <summary>Every construct the table states.</summary>
  public static IReadOnlyList<JavaScriptArrayMember> Members { get; } = [
    new("a.map(cb)", "a loop into a new array of the callback's results"),
    new("a.filter(cb)", "a loop into a new array of the elements the callback is truthy of"),
    new("a.forEach(cb)", "a loop that calls the callback"),
    new("a.reduce(cb), a.reduce(cb, initial)", "a loop that folds from the first element or the initial value; a TypeError for an empty array without one"),
    new("a.reduceRight(cb), a.reduceRight(cb, initial)", "the same loop from the last element"),
    new("a.some(cb), a.every(cb)", "a loop that stops at the first element that decides it"),
    new("a.find(cb), a.findIndex(cb)", "a loop that returns the first element, or its index, the callback is truthy of"),
    new("a.flatMap(cb) with a callback that returns an array", "a loop that appends the elements of every array the callback returns"),
    new("a.sort(cmp) with a comparator without effects", "the stable merge sort of the indices by the sign of the comparator, then the elements written back"),
    new("a.indexOf(v), a.includes(v)", "ListIndexOfStrict, ListIncludesSameValueZero (#1328)"),
    new("a callback that is an arrow, a function expression or a function of the file, taking (element, index, array) as far as its parameters reach", "an IrLambda or an IrFunctionRef"),
    new("a.sort()", "", "sort() without a comparator, which orders the elements by their text"),
    new("a.sort(cmp) with a comparator that writes anything but its own locals or calls a function", "", "a sort comparator with effects, whose call order ECMA-262 leaves to the engine"),
    new("a.findLast(cb), a.findLastIndex(cb)", "", "of an array, which ECMAScript 2023 added"),
    new("a.copyWithin(…)", "", "copyWithin of an array"),
    new("a callback that is a function of the library, such as parseInt", "", "as the callback of"),
    new("a callback with more parameters than the method passes", "", "a callback of"),
    new("a thisArg", "", "with a thisArg"),
    new("a method with a callback on a typed array", "", "of a typed array")
  ];

  /// <summary>The entries the frontend imports.</summary>
  public static IEnumerable<JavaScriptArrayMember> Supported => Members.Where(member => member.Refusal.Length == 0);

  /// <summary>The entries the frontend refuses.</summary>
  public static IEnumerable<JavaScriptArrayMember> Refused => Members.Where(member => member.Refusal.Length > 0);

  /// <summary>Why a method of an array is refused whatever its arguments, or <see langword="null"/> for one that is not.</summary>
  /// <param name="method">The name of the method.</param>
  /// <returns>The refusal.</returns>
  public static string? Refusal(string method) => method switch {
    "findLast" or "findLastIndex" => $"{method} of an array, which ECMAScript 2023 added",
    "copyWithin" => "copyWithin of an array",
    _ => null
  };

}
