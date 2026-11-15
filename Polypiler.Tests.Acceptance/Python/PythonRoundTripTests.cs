using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;

namespace Polypiler.Tests.Acceptance.Python;

/// <summary>
/// Python to IR to Python: every fixture of the Python frontend is imported, written back by the
/// Python backend, and the written module runs every call of the fixture under the pinned CPython
/// through the oracle that recorded the original. Each call has the outcome CPython recorded for the
/// original source, except where the import states a difference (<see cref="PythonOutcome.Expected"/>:
/// an <c>int</c> beyond 64 bits, a complex power). A fixture with a construct the backend does not
/// write yet is refused with diagnostics of the backend's own that name it, as the backend's
/// differential tests accept, and is compared call by call as soon as the backend writes it.
/// </summary>
[TestFixture, Layer("pyperl")]
[Category("EndToEnd")]
public sealed class PythonRoundTripTests {

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() => this._directory = Directory.CreateTempSubdirectory("polypiler-python-round-").FullName;

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  private static string[] Fixtures => PythonFixture.Names;

  [TestCaseSource(nameof(Fixtures)), RequiresToolchain("python")]
  public void Fixture_WrittenBackAsPython_DoesWhatCPythonDidWithTheOriginal(string fixture) {
    var module = PythonFixture.Import(fixture);
    var translated = PythonFixtures.Translate(module);
    if (translated.Source is null) {
      Assert.That(translated.Problems, Is.Not.Empty.And.All.Match(@"^error PLY18(5[01]|58|74): "), "a refusal names its construct with a code of the Python backend");
      TestContext.Out.WriteLine($"{fixture} is refused by the backend: {string.Join("; ", translated.Problems.Distinct().Take(10))}");
      return;
    }

    Assert.That(translated.Problems, Is.Empty);
    var path = Path.Combine(this._directory, fixture + ".py");
    File.WriteAllText(path, translated.Source);

    var calls = PythonCases.For(fixture, module);
    var outcomes = PythonOracle.Outcomes(path, [.. calls.Select(call => call with { Function = translated.Names![call.Function] })]);
    var recorded = PythonFixture.RecordedLines(fixture);
    var mismatches = new List<string>();
    for (var index = 0; index < calls.Count; ++index) {
      var expected = PythonOutcome.Expected(recorded[index][(recorded[index].IndexOf(" => ", StringComparison.Ordinal) + 4)..]);
      if (outcomes[index] != expected)
        mismatches.Add($"{calls[index]}: written back {outcomes[index]}, CPython {expected}");
    }

    TestContext.Out.WriteLine($"{fixture}: {calls.Count} calls, {mismatches.Count} mismatches");
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} of {calls.Count} calls differ\n{translated.Source}");
  }

}
