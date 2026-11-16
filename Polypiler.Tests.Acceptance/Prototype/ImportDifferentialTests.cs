using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The import itself against the original runtime (#1367): every public function of every source of
/// the prototype fixtures is called with its argument sets, in .NET's build of the C# fixture or node's
/// run of the JavaScript original, and in the IR interpreter on the imported module. The outcomes are
/// equal, or the function is named in <see cref="ImportDifferences"/> with the caveat the import
/// announces for the source, or with the oracle difference <c>docs/reference/import-differences.md</c>
/// documents. A backend's differential holds such a call to the interpreter instead of the original.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd")]
[Layer("core")]
[Category("Prototype")]
[NonParallelizable]
public sealed class ImportDifferentialTests {

  /// <summary>Every C# and JavaScript source of the prototype fixtures.</summary>
  public static IEnumerable<string> Sources => GoldenOutputs.All;

  [TestCaseSource(nameof(Sources))]
  public void Fixture_InterpretsAsInDotNetOrNode(string source) {
    var run = FixtureCalls.Run(source);
    if (run.Module is null) {
      Assert.Pass("the frontend refuses the source");
      return;
    }

    var announced = ImportDifferences.Of(source);
    var differing = Enumerable.Range(0, run.Calls.Count).Except(run.Agreeing).ToList();
    var unexplained = differing.Where(index => announced.All(entry => entry.Function != run.Calls[index].Method))
      .Select(index => $"{run.Calls[index]}: original {run.Expected[index]}, interpreted {run.Interpreted[index] ?? FixtureRun.Endless}").ToList();
    TestContext.Out.WriteLine($"{source}: {run.Calls.Count} calls, {differing.Count} where the import differs from the original, {unexplained.Count} of them not announced");
    Assert.That(unexplained, Is.Empty, string.Join("\n", unexplained.Select(line => line[..line.IndexOf('(')]).Distinct()));

    // Every function named differs on some call still, with the reason the name gives.
    foreach (var (function, reason) in announced) {
      Assert.That(differing.Any(index => run.Calls[index].Method == function), $"{function} no longer differs: name it no more");
      Assert.That(ImportDifferences.Holds(run, function), $"{function}: '{reason}' is neither a caveat of the import nor documented");
    }
  }

}
