using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Python;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The Python frontend with the pinned CPython: the recordings the unit tests compare with are what
/// CPython does with the original sources, the recorded trees are what the helper prints today, and
/// <c>polypiler translate --from python</c> parses through the interpreter, translates the fixtures
/// and refuses what CPython or the subset refuses with the exit codes of the command.
/// </summary>
[TestFixture, Layer("pyperl")]
public sealed class PythonFrontendTests {

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() => this._directory = Directory.CreateTempSubdirectory("polypiler-python-").FullName;

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  private static string[] Fixtures => PythonFixture.Names;

  /// <summary>
  /// The outcomes the unit tests compare the interpreter and the emitted C# with are what CPython does
  /// with the original source. Run with <c>POLYPILER_RECORD=1</c> to write the recordings after a fixture changed.
  /// </summary>
  [TestCaseSource(nameof(Fixtures)), RequiresToolchain("python")]
  public void Recording_IsWhatCPythonDoesWithTheOriginal(string fixture) {
    var calls = PythonCases.For(fixture, PythonFixture.Import(fixture));
    var outcomes = PythonOracle.Outcomes(PythonFixture.PathOf(fixture), calls);
    var lines = calls.Select((call, index) => PythonFixture.Line(call, outcomes[index])).ToArray();
    if (PythonFixture.Recording)
      PythonFixture.Record(PythonFixture.RecordingOf(fixture), lines);

    Assert.That(PythonFixture.RecordedLines(fixture), Is.EqualTo(lines));
  }

  /// <summary>Every recorded tree - of a fixture or of a snippet of the unit tests - is what the helper prints for its source today.</summary>
  [Test, RequiresToolchain("python")]
  public void RecordedTrees_AreWhatTheHelperPrints() {
    var trees = PythonFixture.RecordedTrees().ToList();
    Assert.That(trees, Is.Not.Empty);
    foreach (var (path, source, dump) in trees) {
      var (json, failure) = PythonFrontend.Dump("snippet.py", source);
      Assert.That(json, Is.Not.Null, failure);
      Assert.That(PythonFixture.Comparable(json!), Is.EqualTo(dump), path);
    }
  }

  /// <summary>Every fixture imports through the interpreter exactly as from its recorded tree.</summary>
  [TestCaseSource(nameof(Fixtures)), RequiresToolchain("python")]
  public void Fixture_ImportsThroughCPythonAsFromItsRecordedTree(string fixture) {
    var live = PythonFrontend.Import(fixture + ".py", PythonFixture.SourceOf(fixture));
    Assert.That(live.Diagnostics, Is.Empty);
    Assert.That(live.Python, Does.Match(@"^3\.\d+\.\d+$"));
    Assert.That(Core.IR.IrModuleFormat.Write(live.Module!), Is.EqualTo(Core.IR.IrModuleFormat.Write(PythonFixture.Import(fixture))));
  }

  /// <summary>Each fixture translates with the executable into C# and JavaScript, with the caveats of the import as notes.</summary>
  [TestCaseSource(nameof(Fixtures)), RequiresToolchain("python")]
  public void Fixture_TranslatesWithTheExecutable(string fixture) {
    foreach (var target in new[] { "csharp", "javascript" }) {
      var (exitCode, output) = Toolchain.RunPolypiler(this._directory, "translate", PythonFixture.PathOf(fixture), "--from", "python", "--to", target, "--output", "out-" + target);
      Assert.That(exitCode, Is.Zero, output);
      Assert.That(output, Does.Contain("note py-int-64"));
      Assert.That(Directory.EnumerateFiles(Path.Combine(this._directory, "out-" + target)).Select(Path.GetFileName), Does.Contain("polypiler.manifest.json"));
    }
  }

  /// <summary>Scenario: code python rejects is rejected, with python's message, exit code 4 and no output.</summary>
  [Test, RequiresToolchain("python")]
  public void SyntaxError_IsExitCode4WithPythonsMessage() {
    File.WriteAllText(Path.Combine(this._directory, "broken.py"), "def f(:\n");
    var (exitCode, output) = Toolchain.RunPolypiler(this._directory, "translate", "broken.py", "--from", "python", "--to", "csharp", "--output", "out");
    Assert.That(exitCode, Is.EqualTo(4), output);
    Assert.That(output, Does.Match(@"broken\.py\(1,\d+\): error PLY1800: SyntaxError: "));
    Assert.That(Directory.Exists(Path.Combine(this._directory, "out")), Is.False);
  }

  /// <summary>Scenario: an unsupported numeric type is refused, exit code 3, the construct named.</summary>
  [Test, RequiresToolchain("python")]
  public void UnsupportedConstruct_IsExitCode3() {
    File.WriteAllText(Path.Combine(this._directory, "complex.py"), "def f(a: float) -> float:\n    return a + 2j\n");
    var (exitCode, output) = Toolchain.RunPolypiler(this._directory, "translate", "complex.py", "--from", "python", "--to", "javascript", "--output", "out");
    Assert.That(exitCode, Is.EqualTo(3), output);
    Assert.That(output, Does.Contain("complex.py(2,16): error PLY1802: '2j' is not supported by the Python frontend"));
  }

}
