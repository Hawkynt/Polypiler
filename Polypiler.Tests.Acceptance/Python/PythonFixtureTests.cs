using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Tests.Acceptance.Infra;
using Polypiler.Tests.Acceptance.Prototype;

namespace Polypiler.Tests.Acceptance.Python;

/// <summary>
/// Every source of the prototype fixtures, C# and JavaScript, translated to Python and run by the
/// pinned CPython: each public function of the module whose parameters and result cross the boundary
/// as numbers, truth values, strings, arrays of them, structs or optional numbers is called with
/// arguments drawn from fixed sets, and has the outcome .NET's build of the C# fixture or node's run of
/// the JavaScript original has - its result, the arrays it was given afterwards, or the trap it raised.
/// The interpreter's outcome is the oracle only where node cannot run the original and for unsafe C#,
/// whose pointers .NET does not bound as the IR's are. A source the
/// backend refuses names each construct with a code of the Python backend.
/// </summary>
[TestFixture, Layer("pyperl")]
[Category("EndToEnd")]
[NonParallelizable]
public sealed class PythonFixtureTests {

  /// <summary>Every C# and JavaScript source of the prototype fixtures.</summary>
  public static IEnumerable<string> Sources => GoldenOutputs.All;

  [TestCaseSource(nameof(Sources))]
  [RequiresToolchain("python")]
  public async Task Fixture_RunsInCPythonAsInDotNetOrNode(string source) {
    var run = FixtureCalls.Run(source);
    if (run.Module is null) {
      Assert.Pass("the frontend refuses the source");
      return;
    }

    var translated = PythonFixtures.Translate(run.Module);
    if (translated.Source is null) {
      Assert.That(translated.Problems, Is.Not.Empty.And.All.Match(@"^error PLY18\d\d: "), "a refusal names its construct with a code of the Python backend");
      Assert.Inconclusive($"refused: {string.Join("; ", translated.Problems)}");
      return;
    }

    if (run.Calls.Count == 0) {
      Assert.Pass("no function of the module crosses the boundary with values the driver writes");
      return;
    }

    // The original is the oracle of every call but those where the import differs from it on purpose,
    // named in ImportDifferences with an announced caveat or a documented oracle difference: there the
    // backend computes what the IR does. Any other difference fails ImportDifferentialTests, and is
    // compared with the original here. A call the interpreter runs on and on for would run on in CPython
    // as well; that test fails for it too, and it is not started here.
    var expected = ImportDifferences.Expected(run);
    var compared = Enumerable.Range(0, run.Calls.Count).Where(index => run.Interpreted[index] is not null).ToList();
    var outcomes = await PythonDriver.OutcomesAsync(translated.Source, [.. compared.Select(index => run.Calls[index] with { Method = translated.Names![run.Calls[index].Method] })]);
    var mismatches = compared.Select((index, at) => (index, at)).Where(entry => !FixtureCalls.Same(expected[entry.index], outcomes[entry.at]))
      .Select(entry => $"{run.Calls[entry.index]}: expected {expected[entry.index]}, python {outcomes[entry.at]}").ToList();
    TestContext.Out.WriteLine($"{source}: {compared.Count} calls compared, {mismatches.Count} mismatches");
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} of {compared.Count} calls differ\n{translated.Source}");
  }

}
