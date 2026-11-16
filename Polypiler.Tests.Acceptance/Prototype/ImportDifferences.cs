using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The calls of the prototype fixtures where the import differs from the original runtime on purpose
/// (#1367), by source and function: each with the caveat the import announces for the source, or as a
/// difference of the original runtime that <c>docs/reference/import-differences.md</c> documents.
/// <see cref="ImportDifferentialTests"/> holds the list to what the import does - a function named that
/// no longer differs fails there; a backend's differential compares such a call with the IR's outcome,
/// which is what the backend has to compute.
/// </summary>
internal static class ImportDifferences {

  /// <summary>The reason of a difference that the original runtime makes, documented in the reference page.</summary>
  public const string Oracle = "oracle";

  /// <summary>
  /// The functions whose calls may differ, by source: what announces the difference - a caveat of the
  /// import - or <see cref="Oracle"/> for one of the original runtime.
  /// </summary>
  private static readonly Dictionary<string, (string Function, string Reason)[]> _announced = new(StringComparer.Ordinal) {
    // .NET names the namespace in a default message; the IR's classes have none.
    ["exceptions/Classes.cs"] = [("DefaultMessage", Oracle)],
    // RyuJIT rotates (x << n) ^ (x >> (32 - n)) also at n = 0 (#1310).
    ["recognize/RecognizeRotate.cs"] = [("XorVariable", Oracle)],
    // new string(c, -1): .NET throws ArgumentOutOfRangeException, StrRepeat traps with integer overflow.
    ["strings/Traps.cs"] = [("Repeat", Oracle)],
    // An element returned as it is: undefined in JavaScript.
    ["js/arrays.mjs"] = [("readByte", JsOutOfRange), ("readInt", JsOutOfRange), ("readUint", JsOutOfRange), ("readAtUnsignedIndex", JsOutOfRange), ("readAtByteIndex", JsOutOfRange), ("readAtSum", JsOutOfRange),
      ("writeByte", JsOutOfRange), ("writeInt", JsOutOfRange), ("writeUint", JsOutOfRange), ("writeSum", JsOutOfRange), ("scramble", JsOutOfRange)],
    // The remainder of two uint32 not under a coercion is NaN for a divisor of 0 (#1375).
    ["js/operators.mjs"] = [("remainderUint", JsDivision)],
    // A counter array of length 0 stepped: undefined + 1 is NaN, stored into an Array.
    ["js/statements.mjs"] = [("both", JsOutOfRange), ("order", JsOutOfRange)],
    // A string table read past its end, returned as it is.
    ["strings/tokens.js"] = [("keyword", JsOutOfRange)],
    // codePointAt outside the string is undefined.
    ["strings/traps.js"] = [("point", JsOutOfRange)],
    // A store into a JSDoc uint8[] nothing decided, past the end of a short array: an Array would grow.
    ["spans/window.mjs"] = [("shared", JsOutOfRange)],
  };

  private const string JsOutOfRange = "js-oob-read";

  private const string JsDivision = "js-div-zero";

  /// <summary>The functions of a source whose calls may differ, each with its reason.</summary>
  public static IReadOnlyList<(string Function, string Reason)> Of(string source) => _announced.GetValueOrDefault(source) ?? [];

  private static readonly Lazy<string> _reference = new(() => File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "docs", "reference", "import-differences.md")));

  /// <summary>
  /// Whether a function of a run is named with a reason that holds: a caveat the import announced for
  /// the source, or an oracle difference the reference page documents by the function's name.
  /// </summary>
  public static bool Holds(FixtureRun run, string function)
    => Of(run.Source).Any(entry => entry.Function == function && (entry.Reason == Oracle ? _reference.Value.Contains(function, StringComparison.Ordinal) : run.Caveats.Contains(entry.Reason)));

  /// <summary>
  /// What a backend has to compute for each call of a run: the original's outcome, or the
  /// interpreter's where the import differs from the original on a function named with a reason that holds.
  /// </summary>
  public static IReadOnlyList<string> Expected(FixtureRun run) {
    var agreeing = run.Agreeing.ToHashSet();
    return [.. run.Calls.Select((call, index) => !agreeing.Contains(index) && run.Interpreted[index] is { } interpreted && Holds(run, call.Method) ? interpreted : run.Expected[index])];
  }

}
