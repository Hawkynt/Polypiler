using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The import itself against the original runtime (#1367): every public function of every source of
/// the prototype fixtures is called with its argument sets, in .NET's build of the C# fixture or node's
/// run of the JavaScript original, and in the IR interpreter on the imported module. The outcomes are
/// equal, or the function is named below with the caveat the import announces for the source, or with
/// the oracle difference <c>docs/reference/import-differences.md</c> documents. No differential test of
/// a backend leaves a call out because the import already differs.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd")]
[Layer("core")]
[Category("Prototype")]
[NonParallelizable]
public sealed class ImportDifferentialTests {

  /// <summary>The reason of a difference that the original runtime makes, documented in the reference page.</summary>
  private const string Oracle = "oracle";

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

  /// <summary>Every C# and JavaScript source of the prototype fixtures.</summary>
  public static IEnumerable<string> Sources => GoldenOutputs.All;

  [TestCaseSource(nameof(Sources))]
  public void Fixture_InterpretsAsInDotNetOrNode(string source) {
    var run = FixtureCalls.Run(source);
    if (run.Module is null) {
      Assert.Pass("the frontend refuses the source");
      return;
    }

    var announced = _announced.GetValueOrDefault(source) ?? [];
    var differing = Enumerable.Range(0, run.Calls.Count).Except(run.Agreeing).ToList();
    var unexplained = differing.Where(index => announced.All(entry => entry.Function != run.Calls[index].Method))
      .Select(index => $"{run.Calls[index]}: original {run.Expected[index]}, interpreted {run.Interpreted[index] ?? FixtureRun.Endless}").ToList();
    TestContext.Out.WriteLine($"{source}: {run.Calls.Count} calls, {differing.Count} where the import differs from the original, {unexplained.Count} of them not announced");
    Assert.That(unexplained, Is.Empty, string.Join("\n", unexplained.Select(line => line[..line.IndexOf('(')]).Distinct()));

    // Every function named differs on some call still, with the reason the name gives.
    var reference = File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "docs", "reference", "import-differences.md"));
    foreach (var (function, reason) in announced) {
      Assert.That(differing.Any(index => run.Calls[index].Method == function), $"{function} no longer differs: name it no more");
      Assert.That(reason == Oracle ? reference.Contains(function, StringComparison.Ordinal) : run.Caveats.Contains(reason), $"{function}: '{reason}' is neither a caveat of the import nor documented");
    }
  }

}
