using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Core.Verification.Manifests;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Recognize;

/// <summary>
/// The pipeline options of <c>translate</c> (ADR 0004): <c>--opt none|balanced|speed|size|memory</c>, the repeatable
/// list <c>--disable-opt</c>, and <c>--explain-opt</c>, which lists every rule application on the
/// error stream. Test design 9 and 11.
/// </summary>
[TestFixture]
public sealed class OptimizationOptionsTests {

  private const string Rotate = "public static uint F(uint x) => (x << 8) | (x >> 24);";

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  /// <summary>Translates a method to a target; the exit code, the written file or the empty text, and the error stream.</summary>
  private (int ExitCode, string Text, string Error) Translate(string methods, string to, params string[] further) {
    var source = Path.Combine(this._directory, "Snippet.cs");
    File.WriteAllText(source, PrototypeFixture.Wrap(methods));
    var output = Path.Combine(this._directory, "out-" + Guid.NewGuid().ToString("N"));
    using var standard = new StringWriter();
    using var error = new StringWriter();
    var exitCode = Program.Run(["translate", source, "--from", "csharp", "--to", to, "--output", output, .. further], standard, error);
    var text = Directory.Exists(output) ? File.ReadAllText(Directory.GetFiles(output).Single(path => Path.GetFileName(path) != Manifest.FileName)) : "";
    return (exitCode, text, error.ToString());
  }

  [Test]
  public void ByDefault_TheRulesRun() {
    var (exitCode, text, error) = this.Translate(Rotate, "csharp", "--profile", "net10.0");
    Assert.That((exitCode, error), Is.EqualTo(((int)ExitCode.Success, "")));
    Assert.That(text, Does.Contain("BitOperations.RotateLeft(x, 8)").And.Not.Contain("<< 8"));
  }

  [TestCase("none", false)]
  [TestCase("balanced", true)]
  [TestCase("speed", true)]
  [TestCase("size", true)]
  [TestCase("memory", true)]
  public void Opt_ChoosesTheLevel(string level, bool rotated) {
    // The inlining of the helper - in the IR (O0006) and in the backend (O0500) - would write the rotation as shifts again.
    var (exitCode, text, _) = this.Translate(Rotate, "javascript", "--opt", level, "--disable-opt", "O0006,O0500");
    Assert.That(exitCode, Is.EqualTo((int)ExitCode.Success));
    Assert.That(text.Contains("<< 8", StringComparison.Ordinal), Is.EqualTo(!rotated));
  }

  [TestCase("--disable-opt", "O0100")]
  [TestCase("--disable-opt", "O0106,O0100")]
  public void DisableOpt_SwitchesARuleOff(string option, string list) {
    var (exitCode, text, error) = this.Translate(Rotate, "csharp", "--profile", "net10.0", "--explain-opt", option, list);
    Assert.That((exitCode, error), Is.EqualTo(((int)ExitCode.Success, "")));
    Assert.That(text, Does.Contain("<< 8").And.Not.Contain("RotateLeft"));
  }

  [Test]
  public void DisableOpt_MayBeRepeated() {
    var (exitCode, text, _) = this.Translate(Rotate + " public static int G(int x) => x < 0 ? -x : x;", "csharp", "--profile", "net10.0",
      "--disable-opt", "O0100", "--disable-opt", "O0121", "--explain-opt");
    Assert.That(exitCode, Is.EqualTo((int)ExitCode.Success));
    Assert.That(text, Does.Not.Contain("RotateLeft").And.Not.Contain("AbsWrap"));
  }

  [Test]
  public void ExplainOpt_ListsEveryApplicationOnTheErrorStream() {
    var (exitCode, _, error) = this.Translate(Rotate + " public static int G(int x) => x < 0 ? -x : x;", "javascript", "--explain-opt", "--disable-opt", "O0500,O0501");
    Assert.That(exitCode, Is.EqualTo((int)ExitCode.Success));
    var lines = error.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToList();
    // The rules first, in the order they applied; then the passes over the helpers legalization bound in.
    Assert.That(lines[0], Does.EndWith("Snippet.cs: opt O0100 rotate from a shift pair: 'F' statement 1"));
    Assert.That(lines[1], Does.EndWith("Snippet.cs: opt O0121 wrapping absolute value from a sign mask or a selection: 'G' statement 1"));
    Assert.That(lines[2], Does.EndWith("Snippet.cs: opt O0006 inlining: 'F' statement 1"));
    Assert.That(lines.Skip(2), Has.All.Matches<string>(line => System.Text.RegularExpressions.Regex.IsMatch(line, @"Snippet\.cs: opt O00(01|03|05|06|23) ")));
  }

  [Test]
  public void WithoutExplainOpt_NothingIsListed() => Assert.That(this.Translate(Rotate, "javascript").Error, Is.Empty);

  /// <summary>O0127 asks the backend's table: C# prefers the remainder from the quotient, JavaScript does not.</summary>
  [TestCase("csharp", true)]
  [TestCase("javascript", false)]
  public void RemainderFromQuotient_FollowsTheTarget(string to, bool fired) {
    var (exitCode, _, error) = this.Translate("public static int F(int a, int b) { int q = a / b; return q + a % b; }", to, "--explain-opt");
    Assert.That(exitCode, Is.EqualTo((int)ExitCode.Success));
    Assert.That(error.Contains("O0127", StringComparison.Ordinal), Is.EqualTo(fired));
  }

  [TestCase("--opt", "fast", "PLY0012: 'fast' is not an optimization level; expected one of: none, balanced, speed, size, memory")]
  [TestCase("--opt", "None", "PLY0012: 'None' is not an optimization level")]
  [TestCase("--opt", "default", "PLY0012: 'default' is not an optimization level")]
  [TestCase("--disable-opt", "O9999x", "PLY0008: 'O9999x' is not an optimization ID (expected O followed by four digits)")]
  [TestCase("--disable-opt", "o0100", "PLY0008: 'o0100' is not an optimization ID")]
  [TestCase("--disable-opt", "O9999", "PLY0008: unknown optimization 'O9999'")]
  [TestCase("--disable-opt", "", "PLY0003: option '--disable-opt' has an empty list element")]
  [TestCase("--disable-opt", "O0100,,O0106", "PLY0003: option '--disable-opt' has an empty list element")]
  [TestCase("--disable-opt", "O0100,", "PLY0003: option '--disable-opt' has an empty list element")]
  [TestCase("--assume", "Rotate.x 5", "PLY0007: 'Rotate.x 5' is not a valid assumption")]
  public void MalformedOption_IsAUsageError(string option, string value, string message) {
    var (exitCode, text, error) = this.Translate(Rotate, "javascript", option, value);
    Assert.That((exitCode, text), Is.EqualTo(((int)ExitCode.UsageError, "")));
    Assert.That(error, Does.StartWith("polypiler: error " + message).And.Not.Contain("usage:"));
  }

  /// <summary>An assumption of translate is reported, recorded in the manifest's command and warnings, and refused under --strict.</summary>
  [Test]
  public void Assume_IsReportedAndRecorded() {
    var (exitCode, text, error) = this.Translate(Rotate, "javascript", "--assume", "F.x in [0,0xFF]", "--opt", "speed");
    Assert.That(exitCode, Is.EqualTo((int)ExitCode.Success));
    Assert.That(text, Is.Not.Empty);
    Assert.That(error, Does.Contain("warning PLY0009: assumption 'F.x in [0, 255]' is trusted without proof"));
    var (strictExit, strictText, strictError) = this.Translate(Rotate, "javascript", "--assume", "F.x in [0,0xFF]", "--strict");
    Assert.That((strictExit, strictText), Is.EqualTo(((int)ExitCode.UnsupportedConstruct, "")));
    Assert.That(strictError, Does.Contain("error PLY0010: strict mode: PLY0009 assumption 'F.x in [0, 255]' is trusted without proof"));
  }

  [Test]
  public void DisableOpt_WithoutAValue_IsAUsageError() {
    var (exitCode, _, error) = this.Translate(Rotate, "javascript", "--disable-opt");
    Assert.That(exitCode, Is.EqualTo((int)ExitCode.UsageError));
    Assert.That(error, Does.Contain("PLY0003: option '--disable-opt' requires a value"));
  }

  [Test]
  public void PipelineOptions_ApplyToEveryPair() {
    var script = Path.Combine(this._directory, "rotate.js");
    File.WriteAllText(script, "/** @param {int32} x */ export function f(x) { return (x << 8) | (x >>> 24); }");
    using var standard = new StringWriter();
    using var error = new StringWriter();
    var exitCode = Program.Run(["translate", script, "--from", "javascript", "--to", "csharp", "--output", Path.Combine(this._directory, "js"), "--explain-opt"], standard, error);
    Assert.That(exitCode, Is.EqualTo((int)ExitCode.Success));
    Assert.That(error.ToString(), Does.Contain("opt O0100"));
  }

}
