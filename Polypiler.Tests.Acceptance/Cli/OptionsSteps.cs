using NUnit.Framework;
using Polypiler.Tests.Acceptance.Steps;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tests.Cli.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Cli;

/// <summary>
/// The steps of <c>tests/features/infra/cli-options.feature</c>: before each scenario the test-only
/// command <c>options</c> (beside <c>echo</c> and <c>demand</c>) becomes the command every
/// <c>polypiler</c> line of the scenario runs with, in process.
/// </summary>
[Binding, Scope(Feature = "Pipeline options")]
public sealed class OptionsSteps(CommandLog log, TestCommandSet commands) {

  /// <summary>Registers the test commands.</summary>
  [BeforeScenario(Order = 100)]
  public void RegisterTestCommands() => commands.Registry = OptionsCommand.Registry().Registry;

  /// <summary>Checks that nothing was written to standard output.</summary>
  [Then("^stdout is empty$")]
  public void StdoutIsEmpty() => Assert.That(log.Last.Stdout, Is.Empty);

  /// <summary>Checks standard error exactly, after one trailing line break is taken off.</summary>
  [Then("^stderr is " + StepText.Quoted + "$")]
  public void StderrIs(string text) => Assert.That(CommandSteps.WithoutTrailingNewline(log.Last.Stderr), Is.EqualTo(StepText.Unescape(text)));

}
