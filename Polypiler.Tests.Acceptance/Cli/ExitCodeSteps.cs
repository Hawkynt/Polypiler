using System;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using Polypiler.Tests.Acceptance.Steps;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tests.Cli.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Cli;

/// <summary>
/// The steps of <c>tests/features/infra/cli-exitcodes.feature</c> (issue #23). Before each scenario
/// the test-only command <c>raise</c>, beside <c>echo</c> and <c>demand</c>, becomes the command set
/// every <c>polypiler</c> line of the scenario runs with, in process; <see cref="ExitCodeAgreementTests"/>
/// holds the same lines in a child process to the same results.
/// </summary>
[Binding, Scope(Feature = "Stable exit codes")]
public sealed class ExitCodeSteps(CommandLog log, TestCommandSet commands) {

  /// <summary>Registers <c>raise</c> and the commands of the foundation.</summary>
  [BeforeScenario(Order = 100)]
  public void RegisterRaise() => commands.Registry = RaiseCommand.Registry();

  /// <summary>Checks the last line of standard error.</summary>
  [Then("^the last line of stderr is " + StepText.Quoted + "$")]
  public void LastStderrLineIs(string text)
    => Assert.That(Lines(log.Last.Stderr).LastOrDefault(), Is.EqualTo(StepText.Unescape(text)), log.Last.Stderr);

  /// <summary>Checks that standard error has a number of lines, each one JSON object.</summary>
  [Then(@"^stderr is (\d+) lines, each one JSON object$")]
  public void StderrIsJsonLines(int count) {
    var lines = Lines(log.Last.Stderr);
    Assert.That(lines, Has.Length.EqualTo(count), log.Last.Stderr);
    foreach (var line in lines) {
      using var document = JsonDocument.Parse(line);
      Assert.That(document.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Object), line);
    }
  }

  /// <summary>Checks that standard error does not contain a text.</summary>
  [Then("^stderr does not contain " + StepText.Quoted + "$")]
  public void StderrDoesNotContain(string text) => Assert.That(log.Last.Stderr, Does.Not.Contain(StepText.Unescape(text)));

  /// <summary>Checks standard error exactly, after one trailing line break is taken off.</summary>
  [Then("^stderr is " + StepText.Quoted + "$")]
  public void StderrIs(string text) => Assert.That(CommandSteps.WithoutTrailingNewline(log.Last.Stderr), Is.EqualTo(StepText.Unescape(text)));

  /// <summary>Checks that nothing was written to standard output.</summary>
  [Then("^stdout is empty$")]
  public void StdoutIsEmpty() => Assert.That(log.Last.Stdout, Is.Empty);

  /// <summary>Checks one line of standard output, counted from 1.</summary>
  [Then(@"^stdout line (\d+) is " + StepText.Quoted + "$")]
  public void StdoutLineIs(int number, string text)
    => Assert.That(Lines(log.Last.Stdout).ElementAtOrDefault(number - 1), Is.EqualTo(StepText.Unescape(text)), log.Last.Stdout);

  /// <summary>Checks that one line of standard output is a text, exactly.</summary>
  [Then("^stdout contains the line " + StepText.Quoted + "$")]
  public void StdoutContainsTheLine(string text) => Assert.That(Lines(log.Last.Stdout), Does.Contain(StepText.Unescape(text)), log.Last.Stdout);

  private static string[] Lines(string text) => [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r'))];

}
