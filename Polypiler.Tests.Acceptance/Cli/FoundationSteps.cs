using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Steps;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tests.Cli.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Cli;

/// <summary>
/// The steps of <c>tests/features/infra/cli-foundation.feature</c>. Before each scenario the
/// fixtures of <c>tests/fixtures/cli/foundation</c> are copied into the workspace under the same
/// path, and the test-only commands <c>echo</c> and <c>demand</c> become the commands every
/// <c>polypiler</c> line of the scenario runs with, in process.
/// </summary>
[Binding, Scope(Feature = "Command-line foundation")]
public sealed partial class FoundationSteps(ScenarioWorkspace workspace, CommandLog log, TestCommandSet commands, PolypilerRunner polypiler) {

  private const string Fixtures = "tests/fixtures/cli/foundation";

  private EchoCommand? _echo;

  [GeneratedRegex(@"\[([^\]]*)\]")]
  private static partial Regex Bracketed();

  /// <summary>Copies the fixtures and registers the test commands without diagnostics of their own.</summary>
  [BeforeScenario(Order = 100)]
  public void RegisterTestCommands() {
    foreach (var file in Directory.EnumerateFiles(Path.Combine(PrototypeFixture.RepositoryRoot, Fixtures), "*", SearchOption.AllDirectories))
      workspace.Copy(file, Path.GetRelativePath(PrototypeFixture.RepositoryRoot, file).Replace('\\', '/'));

    this.Register([]);
  }

  private void Register(DiagnosticDescriptor[] reports) {
    var (registry, echo, _) = TestCommands.Registry(reports);
    commands.Registry = registry;
    this._echo = echo;
  }

  /// <summary>Makes <c>echo</c> report a warning and an error of exit code 1 after its line.</summary>
  [Given(@"^the command ""echo"" also reports a test warning and a test error with exit code 1$")]
  public void EchoReportsAWarningAndAnError() => this.Register([TestCommands.TestWarning, TestCommands.TestError]);

  /// <summary>Runs <c>polypiler</c> with arguments written one per pair of brackets, so that one may hold a space.</summary>
  [When(@"^I run ""polypiler"" with the arguments((?: \[[^\]]*\])+)$")]
  public void RunWithArguments(string arguments)
    => polypiler.Run([.. Bracketed().Matches(arguments).Select(match => match.Groups[1].Value)]);

  /// <summary>Checks one line of standard output, counted from 1.</summary>
  [Then(@"^stdout line (\d+) is " + StepText.Quoted + "$")]
  public void StdoutLineIs(int number, string text)
    => Assert.That(Lines(log.Last.Stdout).ElementAtOrDefault(number - 1), Is.EqualTo(StepText.Unescape(text)), log.Last.Stdout);

  /// <summary>Checks that a line of standard output starts with a text.</summary>
  [Then("^stdout contains a line starting with " + StepText.Quoted + "$")]
  public void StdoutHasALineStartingWith(string text)
    => Assert.That(Lines(log.Last.Stdout), Has.Some.StartsWith(StepText.Unescape(text)), log.Last.Stdout);

  /// <summary>Checks that standard output is one line, the version and nothing else, as it has been since the prototype.</summary>
  [Then("^stdout is one line holding the version of polypiler$")]
  public void StdoutIsTheVersion() => Assert.That(Lines(log.Last.Stdout), Is.EqualTo(new[] { ProductInfo.Version }));

  /// <summary>Checks that nothing was written to standard output.</summary>
  [Then("^stdout is empty$")]
  public void StdoutIsEmpty() => Assert.That(log.Last.Stdout, Is.Empty);

  /// <summary>Checks standard error exactly, after one trailing line break is taken off.</summary>
  [Then("^stderr is " + StepText.Quoted + "$")]
  public void StderrIs(string text) => Assert.That(CommandSteps.WithoutTrailingNewline(log.Last.Stderr), Is.EqualTo(StepText.Unescape(text)));

  /// <summary>Checks that standard output is that of another command line, run with the same commands.</summary>
  [Then("^stdout equals the stdout of " + StepText.Quoted + "$")]
  public void StdoutEqualsThatOf(string line) {
    var words = Polypiler.Tests.Acceptance.Support.CommandLine.Split(StepText.Unescape(line));
    Assert.That(words[0], Is.EqualTo("polypiler"));
    var expected = log.Last.Stdout;
    Assert.That(polypiler.Run([.. words.Skip(1)]).Stdout, Is.EqualTo(expected).And.Not.Empty);
  }

  /// <summary>Checks that <c>echo</c> did not run.</summary>
  [Then("^the command was not executed$")]
  public void NotExecuted() => Assert.That(this._echo!.Executions, Is.Zero);

  /// <summary>Checks that standard error is one JSON object with two members of string value.</summary>
  [Then("^stderr is one JSON object with " + StepText.Quoted + " " + StepText.Quoted + " and " + StepText.Quoted + " " + StepText.Quoted + "$")]
  public void StderrIsOneJsonObject(string firstName, string firstValue, string secondName, string secondValue) {
    var lines = Lines(log.Last.Stderr);
    Assert.That(lines, Has.Length.EqualTo(1), log.Last.Stderr);
    using var document = JsonDocument.Parse(lines[0]);
    Assert.That(document.RootElement.GetProperty(StepText.Unescape(firstName)).GetString(), Is.EqualTo(StepText.Unescape(firstValue)));
    Assert.That(document.RootElement.GetProperty(StepText.Unescape(secondName)).GetString(), Is.EqualTo(StepText.Unescape(secondValue)));
  }

  /// <summary>Checks that every line of standard error is one JSON object, and that there is one.</summary>
  [Then("^every line of stderr is one JSON object$")]
  public void EveryStderrLineIsJson() {
    var lines = Lines(log.Last.Stderr);
    Assert.That(lines, Is.Not.Empty);
    foreach (var line in lines) {
      using var document = JsonDocument.Parse(line);
      Assert.That(document.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Object), line);
    }
  }

  /// <summary>Checks that standard error holds the test error and not the test warning.</summary>
  [Then("^stderr holds the error line and no warning line$")]
  public void StderrHoldsOnlyTheError()
    => Assert.That(Lines(log.Last.Stderr), Is.EqualTo(new[] { "polypiler: error PLY0098: test error" }));

  /// <summary>Checks that a file of the workspace still has the bytes of its fixture.</summary>
  [Then("^" + StepText.Quoted + " is unchanged$")]
  public void Unchanged(string path) {
    var relative = StepText.Unescape(path);
    Assert.That(File.ReadAllBytes(workspace.Resolve(relative)), Is.EqualTo(File.ReadAllBytes(Path.Combine(PrototypeFixture.RepositoryRoot, relative))));
  }

  private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToArray();

}
