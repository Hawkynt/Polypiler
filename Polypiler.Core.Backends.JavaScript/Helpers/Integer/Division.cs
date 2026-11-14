// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;

namespace Polypiler.Core.Backends.JavaScript;

internal static partial class JavaScriptHelpers {

  private const string Zero64 = "{ hi: 0, lo: 0 }";
  private const string One64 = "{ hi: 0, lo: 1 }";

  /// <summary>
  /// The floor and Euclidean divisions of signed integers, with the traps of the division: a zero
  /// divisor and the smallest value divided by -1. Of up to 32 bits the quotient of two numbers is
  /// rounded down exactly, as its fraction is never closer to an integer than one over the divisor,
  /// and the remainder of two numbers is exact; of 64 bits the <c>BigInt</c> division or the one of
  /// the halves is corrected by one.
  /// </summary>
  private static IEnumerable<(string Name, Func<Func<string, bool>, string> Text)> DivisionHelpers => [
    Same("$divf", Divides("$divf", "return Math.floor(a / b) | 0;")),
    Same("$modf", Divides("$modf", "@const r = a % b;", "return (r !== 0 && (r < 0) !== (b < 0) ? r + b : r) | 0;")),
    Same("$dive", Divides("$dive", "@const r = a % b;", "@const q = (a - r) / b;", "return (r < 0 ? (b > 0 ? q - 1 : q + 1) : q) | 0;")),
    Same("$mode", Divides("$mode", "@const r = a % b;", "return (r < 0 ? r + Math.abs(b) : r) | 0;")),
    Wide("$divf64",
      Divides64("$divf64", "return a % b !== 0n && (a < 0n) !== (b < 0n) ? a / b - 1n : a / b;"),
      Lines(
        "function $divf64(a, b) {",
        "  @const q = $div64(a, b);",
        "  @const r = $rem64(a, b);",
        $"  return (r.hi | r.lo) !== 0 && (r.hi >= 2147483648) !== (b.hi >= 2147483648) ? $sub64(q, {One64}) : q;",
        "}")),
    Wide("$modf64",
      Divides64("$modf64", "@const r = a % b;", "return r !== 0n && (r < 0n) !== (b < 0n) ? r + b : r;"),
      Lines(
        "function $modf64(a, b) {",
        "  @const r = $rem64(a, b);",
        "  return (r.hi | r.lo) !== 0 && (r.hi >= 2147483648) !== (b.hi >= 2147483648) ? $add64(r, b) : r;",
        "}")),
    Wide("$dive64",
      Divides64("$dive64", "@const q = a / b;", "return a % b < 0n ? (b > 0n ? q - 1n : q + 1n) : q;"),
      Lines(
        "function $dive64(a, b) {",
        "  @const q = $div64(a, b);",
        "  if ($rem64(a, b).hi < 2147483648) return q;",
        $"  return b.hi < 2147483648 ? $sub64(q, {One64}) : $add64(q, {One64});",
        "}")),
    Wide("$mode64",
      Divides64("$mode64", "@const r = a % b;", "return r < 0n ? (b < 0n ? r - b : r + b) : r;"),
      Lines(
        "function $mode64(a, b) {",
        "  @const r = $rem64(a, b);",
        "  if (r.hi < 2147483648) return r;",
        "  return b.hi >= 2147483648 ? $sub64(r, b) : $add64(r, b);",
        "}"))
  ];

  private static string Divides(string name, params string[] body)
    => Lines([$"function {name}(a, b, low) {{", ZeroDivisor, "  if (a === low && b === -1) " + Overflow, .. Array.ConvertAll(body, line => "  " + line), "}"]);

  private static string Divides64(string name, params string[] body)
    => Lines([$"function {name}(a, b) {{", ZeroDivisor64, Unrepresentable64, .. Array.ConvertAll(body, line => "  " + line), "}"]);

}
