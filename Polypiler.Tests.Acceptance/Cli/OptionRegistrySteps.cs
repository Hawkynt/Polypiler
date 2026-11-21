using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Cli.Options;
using Polypiler.Tests.Acceptance.Steps;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tests.Cli.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Cli;

/// <summary>
/// The steps of <c>tests/features/infra/cli-option-registry.feature</c> (issue #178): each Given
/// registers test-only contributors with the commands <c>probe</c>, <c>echo</c> and <c>translate</c>,
/// which every <c>polypiler</c> line of the scenario then runs with, in process.
/// </summary>
[Binding, Scope(Feature = "Option and identifier contributions")]
public sealed class OptionRegistrySteps(CommandLog log, TestCommandSet commands, PolypilerRunner polypiler) {

  private ProbeCommand? _probe;
  private OptionContributions? _contributions;
  private InvalidOperationException? _refusal;

  private void Register(params object[] contributors) {
    var (registry, contributions, probe) = ProbeCommand.Setup(contributors);
    commands.Registry = registry;
    commands.Contributions = contributions;
    this._contributions = contributions;
    this._probe = probe;
  }

  /// <summary>Registers <c>--demo-level &lt;n&gt;</c> and <c>--demo-flag</c> on <c>probe</c>.</summary>
  [Given(@"^a contributor owned by " + StepText.Quoted + @" that adds ""--demo-level <n>"" and ""--demo-flag"" to the command ""probe""$")]
  public void LevelAndFlag(string owner) => this.Register(DemoContributor.Level(StepText.Unescape(owner), "probe"));

  /// <summary>Registers the set-list, the address and the flag that is not recorded on <c>probe</c>.</summary>
  [Given(@"^a contributor owned by " + StepText.Quoted + @" that adds to ""probe"" the set-list ""--demo-tags"", the single-valued ""--demo-address"" whose normal spelling is ""0x"" followed by four upper-case hexadecimal digits, and the flag ""--demo-trace"" declared not recorded$")]
  public void SpelledOptions(string owner) => this.Register(new DemoContributor(StepText.Unescape(owner), ["probe"],
    new DemoOption(new ContributedOption("--demo-tags", OptionKind.List, ListOrder.Set, true, ContributedOption.Identity)),
    new DemoOption(new ContributedOption("--demo-address", OptionKind.Single, ListOrder.None, true, DemoContributor.Address)),
    new DemoOption(new ContributedOption("--demo-trace", OptionKind.Flag, ListOrder.None, false, ContributedOption.Identity))));

  /// <summary>Registers the contributor of the first scenario for <c>probe</c> only.</summary>
  [Given(@"^the same contributor, registered for ""probe"" only$")]
  public void SameContributor() => this.Register(DemoContributor.Level("DEMO-ISSUE", "probe"));

  /// <summary>Registers a contributor of one option on one command.</summary>
  [Given(@"^a contributor owned by " + StepText.Quoted + " that adds " + StepText.Quoted + " to " + StepText.Quoted + "$")]
  public void OneOption(string owner, string option, string command) => this.Register(One(owner, option, command));

  /// <summary>Registers a second contributor of one option, keeping what it throws.</summary>
  [When(@"^a contributor owned by " + StepText.Quoted + " that adds " + StepText.Quoted + " to " + StepText.Quoted + " is registered$")]
  public void RegisterAnother(string owner, string option, string command) {
    var described = this._contributions!.Describe();
    this._refusal = Assert.Throws<InvalidOperationException>(() => this._contributions.Add(One(owner, option, command)));
    Assert.That(this._contributions.Describe(), Is.EqualTo(described), "a refused contributor registers nothing");
  }

  /// <summary>Checks the message of the refused registration.</summary>
  [Then(@"^registration throws InvalidOperationException with message " + StepText.Quoted + "$")]
  public void RegistrationThrows(string message) => Assert.That(this._refusal?.Message, Is.EqualTo(StepText.Unescape(message)));

  /// <summary>Registers an identifier for one direction.</summary>
  [Given(@"^an identifier contributor that registers " + StepText.Quoted + " for direction " + StepText.Quoted + "$")]
  public void Identifier(string name, string direction)
    => this.Register(new DemoIdentifiers("DEMO-ISSUE", new ContributedIdentifier(StepText.Unescape(name), Enum.Parse<IdentifierDirection>(StepText.Unescape(direction)), "a demo format")));

  /// <summary>Registers the contributor that refuses one value with its own error and exit code.</summary>
  [Given(@"^the contributor reports error ""PLY9001"" with exit code 2 for ""--demo-level 0""$")]
  public void RefusingContributor() {
    this.Register(DemoContributor.Level("DEMO-ISSUE", "probe"));
    Assert.That((DemoContributor.Refused.Code, DemoContributor.Refused.ExitCode), Is.EqualTo(("PLY9001", (Polypiler.Core.ExitCode?)Polypiler.Core.ExitCode.UsageError)));
  }

  /// <summary>Checks the values the contributor of the first scenario read.</summary>
  [Then(@"^the option bag holds demo-level 3 and demo-flag true$")]
  public void BagHolds() {
    var bag = this._probe!.LastBag!;
    Assert.That(bag.GetOrDefault(DemoContributor.ValueKey("--demo-level"), ""), Is.EqualTo("3"));
    Assert.That(bag.GetOrDefault(DemoContributor.FlagKey("--demo-flag"), false), Is.True);
  }

  /// <summary>Checks the options of the last run as the canonical command line writes them.</summary>
  [Then(@"^the canonical arguments are " + StepText.Quoted + "$")]
  public void CanonicalArguments(string expected) => Assert.That(string.Join(' ', this._probe!.LastCanonical), Is.EqualTo(StepText.Unescape(expected)));

  /// <summary>Checks that a help lists a group heading and, on the lines after it, two options in that order.</summary>
  [Then(@"^""polypiler probe --help"" contains the heading " + StepText.Quoted + " followed by " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void HelpHasTheGroup(string heading, string first, string second) {
    var lines = polypiler.Run("probe", "--help").Stdout.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
    var at = Array.IndexOf(lines, StepText.Unescape(heading));
    Assert.That(at, Is.GreaterThan(0), string.Join('\n', lines));
    Assert.That(lines[at + 1].TrimStart(), Does.StartWith(StepText.Unescape(first) + " "));
    Assert.That(lines[at + 2].TrimStart(), Does.StartWith(StepText.Unescape(second) + " "));
  }

  /// <summary>Runs a second command line and checks its exit code and standard error.</summary>
  [Then(@"^""polypiler ([^""]*)"" exits with (\d+) and " + StepText.Quoted + "$")]
  public void OtherLineExits(string line, int exitCode, string text) {
    var result = polypiler.Run([.. Polypiler.Tests.Acceptance.Support.CommandLine.Split(line)]);
    Assert.That(result.ExitCode, Is.EqualTo(exitCode), result.Stderr);
    Assert.That(result.Stderr, Does.Contain(StepText.Unescape(text)));
  }

  /// <summary>Checks that <c>probe</c> did not run.</summary>
  [Then(@"^the command's Execute method is not called$")]
  public void NotExecuted() {
    Assert.That(this._probe!.Executions, Is.Zero);
    Assert.That(log.Last.Stderr, Does.Contain("error PLY9001:"));
  }

  private static DemoContributor One(string owner, string option, string command)
    => new(StepText.Unescape(owner), [StepText.Unescape(command)], new DemoOption(new ContributedOption(StepText.Unescape(option), OptionKind.Single, ListOrder.None, true, ContributedOption.Identity)));

}
