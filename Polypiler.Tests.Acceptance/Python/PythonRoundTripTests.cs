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
/// an <c>int</c> beyond 64 bits, a complex power) and where the IR has no class for Python's exception
/// (<see cref="Expected"/>).
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
    Assert.That(translated.Problems, Is.Empty);
    var path = Path.Combine(this._directory, fixture + ".py");
    File.WriteAllText(path, translated.Source);

    var calls = PythonCases.For(fixture, module);
    var outcomes = PythonOracle.Outcomes(path, [.. calls.Select(call => call with { Function = translated.Names![call.Function] })]);
    var recorded = PythonFixture.RecordedLines(fixture);
    var mismatches = new List<string>();
    for (var index = 0; index < calls.Count; ++index) {
      var expected = Expected(recorded[index][(recorded[index].IndexOf(" => ", StringComparison.Ordinal) + 4)..]);
      if (outcomes[index] != expected)
        mismatches.Add($"{calls[index]}: written back {outcomes[index]}, CPython {expected}");
    }

    TestContext.Out.WriteLine($"{fixture}: {calls.Count} calls, {mismatches.Count} mismatches");
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} of {calls.Count} calls differ\n{translated.Source}");
  }

  /// <summary>
  /// What the written-back module is expected to do, given what CPython did with the original: what
  /// the import states, except that a failing <c>assert</c> raises <c>RuntimeError</c>. The IR has no
  /// class for <c>AssertionError</c>; the import raises <c>InvalidOperationException</c>, which the
  /// backend writes as <c>RuntimeError</c> (docs/reference/exceptions.md, "Known differences").
  /// </summary>
  private static string Expected(string python) {
    var stated = PythonOutcome.Expected(python);
    return stated == "raise AssertionError" ? "raise RuntimeError" : stated;
  }

}
