using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Recognize;

/// <summary>
/// The pipeline options of <c>translate</c> (ADR 0004): <c>--opt none|default</c>, the repeatable
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
    var text = Directory.Exists(output) ? File.ReadAllText(Directory.GetFiles(output).Single()) : "";
    return (exitCode, text, error.ToString());
  }

  [Test]
  public void ByDefault_TheRulesRun() {
    var (exitCode, text, error) = this.Translate(Rotate, "csharp", "--profile", "net10.0");
    Assert.That((exitCode, error), Is.EqualTo((Program.Success, "")));
    Assert.That(text, Does.Contain("BitOperations.RotateLeft(x, 8)").And.Not.Contain("<< 8"));
  }

  [TestCase("none", false)]
  [TestCase("default", true)]
  public void Opt_ChoosesTheLevel(string level, bool rotated) {
    var (exitCode, text, _) = this.Translate(Rotate, "javascript", "--opt", level);
    Assert.That(exitCode, Is.EqualTo(Program.Success));
    Assert.That(text.Contains("<< 8", StringComparison.Ordinal), Is.EqualTo(!rotated));
  }

  [TestCase("--disable-opt", "O0100")]
  [TestCase("--disable-opt", "O0106,O0100")]
  public void DisableOpt_SwitchesARuleOff(string option, string list) {
    var (exitCode, text, error) = this.Translate(Rotate, "csharp", "--profile", "net10.0", "--explain-opt", option, list);
    Assert.That((exitCode, error), Is.EqualTo((Program.Success, "")));
    Assert.That(text, Does.Contain("<< 8").And.Not.Contain("RotateLeft"));
  }

  [Test]
  public void DisableOpt_MayBeRepeated() {
    var (exitCode, text, _) = this.Translate(Rotate + " public static int G(int x) => x < 0 ? -x : x;", "csharp", "--profile", "net10.0",
      "--disable-opt", "O0100", "--disable-opt", "O0121", "--explain-opt");
    Assert.That(exitCode, Is.EqualTo(Program.Success));
    Assert.That(text, Does.Not.Contain("RotateLeft").And.Not.Contain("AbsWrap"));
  }

  [Test]
  public void ExplainOpt_ListsEveryApplicationOnTheErrorStream() {
    var (exitCode, _, error) = this.Translate(Rotate + " public static int G(int x) => x < 0 ? -x : x;", "javascript", "--explain-opt");
    Assert.That(exitCode, Is.EqualTo(Program.Success));
    var lines = error.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToList();
    Assert.That(lines, Has.Count.EqualTo(2));
    Assert.That(lines[0], Does.EndWith("Snippet.cs: opt O0100 rotate from a shift pair: 'F' statement 1"));
    Assert.That(lines[1], Does.EndWith("Snippet.cs: opt O0121 wrapping absolute value from a sign mask or a selection: 'G' statement 1"));
  }

  [Test]
  public void WithoutExplainOpt_NothingIsListed() => Assert.That(this.Translate(Rotate, "javascript").Error, Is.Empty);

  /// <summary>O0127 asks the backend's table: C# prefers the remainder from the quotient, JavaScript does not.</summary>
  [TestCase("csharp", true)]
  [TestCase("javascript", false)]
  public void RemainderFromQuotient_FollowsTheTarget(string to, bool fired) {
    var (exitCode, _, error) = this.Translate("public static int F(int a, int b) { int q = a / b; return q + a % b; }", to, "--explain-opt");
    Assert.That(exitCode, Is.EqualTo(Program.Success));
    Assert.That(error.Contains("O0127", StringComparison.Ordinal), Is.EqualTo(fired));
  }

  [TestCase("--opt", "fast", "unknown optimization level 'fast'; the levels are none and default")]
  [TestCase("--opt", "None", "unknown optimization level 'None'")]
  [TestCase("--disable-opt", "O9999x", "'O9999x' is not an optimization number of the form O####")]
  [TestCase("--disable-opt", "o0100", "'o0100' is not an optimization number")]
  [TestCase("--disable-opt", "O9999", "unknown optimization 'O9999'; the pipeline has O0100, O0101")]
  [TestCase("--disable-opt", "", "the option '--disable-opt' has an empty item")]
  [TestCase("--disable-opt", "O0100,,O0106", "the option '--disable-opt' has an empty item")]
  [TestCase("--disable-opt", "O0100,", "the option '--disable-opt' has an empty item")]
  public void MalformedOption_IsAUsageError(string option, string value, string message) {
    var (exitCode, text, error) = this.Translate(Rotate, "javascript", option, value);
    Assert.That((exitCode, text), Is.EqualTo((Program.UsageError, "")));
    Assert.That(error, Does.Contain("error PLY0001: " + message).And.Contain("usage: polypiler translate"));
  }

  [Test]
  public void DisableOpt_WithoutAValue_IsAUsageError() {
    var (exitCode, _, error) = this.Translate(Rotate, "javascript", "--disable-opt");
    Assert.That(exitCode, Is.EqualTo(Program.UsageError));
    Assert.That(error, Does.Contain("PLY0001: the option '--disable-opt' needs a value"));
  }

  [Test]
  public void PipelineOptions_ApplyToEveryPair() {
    var script = Path.Combine(this._directory, "rotate.js");
    File.WriteAllText(script, "/** @param {int32} x */ export function f(x) { return (x << 8) | (x >>> 24); }");
    using var standard = new StringWriter();
    using var error = new StringWriter();
    var exitCode = Program.Run(["translate", script, "--from", "javascript", "--to", "csharp", "--output", Path.Combine(this._directory, "js"), "--explain-opt"], standard, error);
    Assert.That(exitCode, Is.EqualTo(Program.Success));
    Assert.That(error.ToString(), Does.Contain("opt O0100"));
  }

}
