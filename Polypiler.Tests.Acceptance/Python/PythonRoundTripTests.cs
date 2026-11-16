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
/// a complex power), and a declared exception class counts as the built-in it derives from. Every
/// fixture is written back; none is refused.
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

  /// <summary>
  /// The classes the backend declares for exception classes of the IR Python has no built-in for, each
  /// derived from the built-in a Python import raises for them (docs/reference/exceptions.md, "Python"):
  /// an <c>except TypeError</c> of the written-back module catches an <c>InvalidCastError</c>.
  /// </summary>
  private static readonly Dictionary<string, string> _declared = new(StringComparer.Ordinal) {
    ["raise InvalidCastError"] = "raise TypeError",
    ["raise ArgumentOutOfRangeError"] = "raise ValueError",
    ["raise FormatError"] = "raise ValueError",
    // struct.error is imported as the class StructError, a name every target can have (ClassDeclarations.Exception).
    ["raise StructError"] = "raise error"
  };

  [TestCaseSource(nameof(Fixtures)), RequiresToolchain("python")]
  public void Fixture_WrittenBackAsPython_DoesWhatCPythonDidWithTheOriginal(string fixture) {
    var module = PythonFixture.Import(fixture);
    var translated = PythonFixtures.Translate(module);
    // Every fixture of the frontend is written back: the backend carries all it imports (#446, #448).
    Assert.That(translated.Problems, Is.Empty);
    Assert.That(translated.Source, Is.Not.Null);
    var path = Path.Combine(this._directory, fixture + ".py");
    File.WriteAllText(path, translated.Source);

    var calls = PythonCases.For(fixture, module);
    var outcomes = PythonOracle.Outcomes(path, [.. calls.Select(call => call with { Function = translated.Names![call.Function] })]);
    var recorded = PythonFixture.RecordedLines(fixture);
    var mismatches = new List<string>();
    for (var index = 0; index < calls.Count; ++index) {
      var expected = PythonOutcome.Expected(recorded[index][(recorded[index].IndexOf(" => ", StringComparison.Ordinal) + 4)..]);
      if (_declared.GetValueOrDefault(outcomes[index], outcomes[index]) != expected)
        mismatches.Add($"{calls[index]}: written back {outcomes[index]}, CPython {expected}");
    }

    TestContext.Out.WriteLine($"{fixture}: {calls.Count} calls, {mismatches.Count} mismatches");
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} of {calls.Count} calls differ\n{translated.Source}");
  }

}
