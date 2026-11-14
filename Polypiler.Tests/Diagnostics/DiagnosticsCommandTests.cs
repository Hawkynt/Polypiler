using System.IO;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling;
using Polypiler.Tooling.Commands;

namespace Polypiler.Tests.Diagnostics;

/// <summary><c>polypiler-tooling diagnostics validate</c> in process: its summary line, its problems and its exit codes.</summary>
[TestFixture]
public sealed class DiagnosticsCommandTests {

  private static readonly string _newLine = System.Environment.NewLine;

  private static string Fixture(string name) => Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "infra", "diagnostics", name);

  private static (int ExitCode, string Output, string Error) Run(params string[] args) {
    var (output, error) = (new StringWriter(), new StringWriter());
    var exitCode = Program.Run([DiagnosticsCommand.Verb, .. args], output, error);
    return (exitCode, output.ToString(), error.ToString());
  }

  [Test]
  public void ValidRegistry_PrintsTheCountAndSucceeds() {
    var (exitCode, output, error) = Run("validate", "--registry", Fixture("valid.yaml"));
    Assert.That((exitCode, output, error), Is.EqualTo((0, "5 codes, 0 problems" + _newLine, "")));
  }

  [Test]
  public void InvalidRegistry_ReportsEachProblemAndFails() {
    var (exitCode, output, error) = Run("validate", "--registry", Fixture("duplicate.yaml"));
    Assert.That((exitCode, output), Is.EqualTo((1, "1 code, 1 problem" + _newLine)));
    Assert.That(error, Is.EqualTo($"{Fixture("duplicate.yaml")}(14,5): error PLY0045: duplicate code PLY0030" + _newLine));
  }

  [Test]
  public void WithoutRegistryOption_ReadsTheRepositoryPathBelowTheCurrentDirectory() {
    var (output, error) = (new StringWriter(), new StringWriter());
    Assert.That(DiagnosticsCommand.Run(["validate"], output, error, PrototypeFixture.RepositoryRoot), Is.Zero, error.ToString());
    Assert.That(output.ToString(), Does.EndWith(" codes, 0 problems" + _newLine));
  }

  [TestCase(new string[0], "diagnostics needs 'validate'")]
  [TestCase(new[] { "check" }, "unknown diagnostics command 'check'")]
  [TestCase(new[] { "validate", "--lock", "x" }, "unknown option '--lock'")]
  [TestCase(new[] { "validate", "--registry" }, "option '--registry' requires a value")]
  [TestCase(new[] { "validate", "--registry", "a", "--registry", "b" }, "option '--registry' is given more than once")]
  public void CommandLine_NotUnderstood(string[] args, string message) {
    var (exitCode, output, error) = Run(args);
    Assert.That((exitCode, output, error), Is.EqualTo((2, "", $"polypiler-tooling: error PLY0003: {message}" + _newLine)));
  }

  [Test]
  public void Usage_ListsTheVerb() {
    var output = new StringWriter();
    Program.Run([], output, new StringWriter());
    Assert.That(output.ToString(), Does.Contain("polypiler-tooling diagnostics validate [--registry <path>]"));
  }

}
