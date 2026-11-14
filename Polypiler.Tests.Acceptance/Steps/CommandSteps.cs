using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Tests.Acceptance.Prototype;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Steps;

/// <summary>
/// The shared steps that run a command line in the scenario's workspace and check what it did. A line
/// whose first word is <c>polypiler</c> goes, without that word, to <see cref="PolypilerRunner"/>; one
/// whose first word is <c>polypiler-tooling</c>, without that word, or a verb of
/// <c>polypiler-tooling</c> starts the <c>polypiler-tooling</c> executable in the workspace through
/// <see cref="Toolchain.Tooling"/>; any other line goes to <see cref="PolypilerRunner"/> as it is.
/// </summary>
[Binding]
public sealed class CommandSteps(PolypilerRunner polypiler, ScenarioWorkspace workspace, CommandLog log) {

  /// <summary>The verbs of <c>polypiler-tooling</c>.</summary>
  public static IReadOnlySet<string> ToolingVerbs { get; } = new HashSet<string>(StringComparer.Ordinal) {
    "toolchains", "spec", "corpus", "licenses", "integrity", "rebuild", "sbom", "report"
  };

  /// <summary>Which program a command line goes to, and the arguments it gets.</summary>
  public static (CommandTarget Target, string[] Arguments) Route(string[] words) => words switch {
    ["polypiler", .. var rest] => (CommandTarget.Polypiler, [.. rest]),
    ["polypiler-tooling", .. var rest] => (CommandTarget.Tooling, [.. rest]),
    [var verb, ..] when ToolingVerbs.Contains(verb) => (CommandTarget.Tooling, [.. words]),
    _ => (CommandTarget.Polypiler, [.. words])
  };

  /// <summary>Runs a command line.</summary>
  [When(@"^I run ""(.*)""$")]
  public void Run(string line) {
    var (target, arguments) = Route([.. CommandLine.Split(line)]);
    if (target == CommandTarget.Polypiler) {
      polypiler.Run(arguments);
      return;
    }

    log.Add(Toolchain.Tooling(workspace.Path, arguments));
  }

  /// <summary>Checks the exit code of the last command line.</summary>
  [Then(@"^the exit code is (-?\d+)$")]
  public void ExitCodeIs(int expected) => Assert.That(log.Last.ExitCode, Is.EqualTo(expected), log.Last.Output);

  /// <summary>Checks that standard output contains a text.</summary>
  [Then("^stdout contains " + StepText.Quoted + "$")]
  public void StdoutContains(string text) => Assert.That(log.Last.Stdout, Does.Contain(StepText.Unescape(text)));

  /// <summary>Checks that standard error contains a text.</summary>
  [Then("^stderr contains " + StepText.Quoted + "$")]
  public void StderrContains(string text) => Assert.That(log.Last.Stderr, Does.Contain(StepText.Unescape(text)));

  /// <summary>Checks standard output exactly, after one trailing line break is taken off.</summary>
  [Then("^stdout is " + StepText.Quoted + "$")]
  public void StdoutIs(string text) => Assert.That(WithoutTrailingNewline(log.Last.Stdout), Is.EqualTo(StepText.Unescape(text)));

  /// <summary>Checks that standard output does not contain a text.</summary>
  [Then("^stdout does not contain " + StepText.Quoted + "$")]
  public void StdoutDoesNotContain(string text) => Assert.That(log.Last.Stdout, Does.Not.Contain(StepText.Unescape(text)));

  /// <summary>Checks that nothing was written to standard error.</summary>
  [Then("^stderr is empty$")]
  public void StderrIsEmpty() => Assert.That(log.Last.Stderr, Is.Empty);

  /// <summary>A text without one trailing line break, written as <c>\n</c> or <c>\r\n</c>.</summary>
  public static string WithoutTrailingNewline(string text)
    => text.EndsWith("\r\n", StringComparison.Ordinal) ? text[..^2] : text.EndsWith('\n') ? text[..^1] : text;

}

/// <summary>The program a command line of a step goes to.</summary>
public enum CommandTarget {

  /// <summary><c>polypiler</c>.</summary>
  Polypiler,

  /// <summary><c>polypiler-tooling</c>.</summary>
  Tooling

}
