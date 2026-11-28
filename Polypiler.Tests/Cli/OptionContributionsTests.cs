using System;
using System.CommandLine;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Cli.Options;
using Polypiler.Core;
using Polypiler.Core.Verification.Manifests;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Cli.Support;

namespace Polypiler.Tests.Cli;

/// <summary>
/// How an area adds options and identifiers to a command (issue #178, test design 1-23): the
/// test-only command <c>probe</c> with demo contributors, in process.
/// </summary>
[TestFixture]
public sealed class OptionContributionsTests {

  private const string Owner = "DEMO-ISSUE";

  private sealed record Run(int ExitCode, string Output, string Error, ProbeCommand Probe) {

    public string Canonical => string.Join(' ', this.Probe.LastCanonical);

  }

  private static Run Invoke(object[] contributors, params string[] args) {
    var (registry, contributions, probe) = ProbeCommand.Setup(contributors);
    using var output = new StringWriter();
    using var error = new StringWriter();
    var exitCode = Program.Run(args, output, error, registry, contributions, PrototypeFixture.RepositoryRoot);
    return new Run(exitCode, output.ToString(), error.ToString(), probe);
  }

  private static DemoOption Declared(string name, OptionKind kind, ListOrder order = ListOrder.None, bool recorded = true, Func<string, string>? spelling = null, string? value = null)
    => new(new ContributedOption(name, kind, order, recorded, spelling ?? ContributedOption.Identity), value);

  private static DemoContributor Demo(params DemoOption[] options) => new(Owner, ["probe"], options);

  private static InvalidOperationException Refused(params object[] contributors)
    => Assert.Throws<InvalidOperationException>(() => ProbeCommand.Setup(contributors))!;

  private static string[] Lines(string text) => text.Split(Environment.NewLine);

  /// <summary>Test design 1 and the first scenario: the bag, the canonical arguments, the help.</summary>
  [Test]
  public void AContributedOption_IsParsedShownAndRenderedCanonically() {
    var run = Invoke([DemoContributor.Level(Owner, "probe")], "probe", "input.txt", "--demo-flag", "--demo-level", "3", "--opt", "speed");
    Assert.That((run.ExitCode, run.Error, run.Output), Is.EqualTo((0, "", "probe input.txt" + Environment.NewLine)));
    Assert.That(run.Probe.LastBag!.GetOrDefault(DemoContributor.ValueKey("--demo-level"), ""), Is.EqualTo("3"));
    Assert.That(run.Probe.LastBag.GetOrDefault(DemoContributor.FlagKey("--demo-flag"), false), Is.True);
    Assert.That(run.Canonical, Is.EqualTo("--opt speed --demo-flag --demo-level 3"));

    var help = Lines(Invoke([DemoContributor.Level(Owner, "probe")], "probe", "--help").Output);
    var heading = Array.IndexOf(help, "Options from DEMO-ISSUE:");
    Assert.That(heading, Is.GreaterThan(Array.IndexOf(help, "options:")).And.LessThan(Array.IndexOf(help, "global options:")));
    Assert.That(help[(heading + 1)..(heading + 3)], Is.EqualTo(new[] { "  --demo-flag       demo flag", "  --demo-level <n>  demo value" }));
    Assert.That(help[..heading], Has.None.Contain("--demo-"), "a contributed option is not one of the command's own");
  }

  /// <summary>Test design 2 and the structural measurement: the shipped contributions collide nowhere and leave the help of translate as it was.</summary>
  [Test]
  public void TheShippedContributions_LeaveTheCommandsAsTheyWere() {
    var registry = Program.CreateRegistry();
    var shipped = Program.CreateContributions(registry);
    Assert.That(Program.CreateContributions().Describe(), Is.EqualTo(shipped.Describe()));
    using var with = new StringWriter();
    using var without = new StringWriter();
    Assert.That(Program.Run(["translate", "--help"], with, TextWriter.Null, registry, shipped, PrototypeFixture.RepositoryRoot), Is.Zero);
    Assert.That(Program.Run(["translate", "--help"], without, TextWriter.Null, registry, PrototypeFixture.RepositoryRoot), Is.Zero);
    Assert.That(with.ToString(), Is.EqualTo(without.ToString()).And.Not.Contain("Options from"));
  }

  /// <summary>Test design 3: an option is known on exactly the commands its contributor names.</summary>
  [Test]
  public void AnOption_IsKnownExactlyOnTheCommandsOfItsContributor() {
    var probeOnly = Invoke([DemoContributor.Level(Owner, "probe")], "echo", "--help");
    Assert.That(probeOnly.Output, Does.Not.Contain("--demo-level"));
    var both = Invoke([DemoContributor.Level(Owner, "probe", "echo")], "echo", "--help");
    Assert.That(both.Output, Does.Contain("Options from DEMO-ISSUE:").And.Contain("--demo-level"));
    var echo = Invoke([DemoContributor.Level(Owner, "probe", "echo")], "echo", "tests/fixtures/cli/foundation/input.txt", "--demo-level", "3");
    Assert.That((echo.ExitCode, echo.Error), Is.EqualTo((0, "")));
    var (_, contributions, _) = ProbeCommand.Setup(DemoContributor.Level(Owner, "probe", "echo"));
    Assert.That(contributions.Describe().Select(option => (option.Command, option.Option.Name)),
      Is.EqualTo(new[] { ("echo", "--demo-flag"), ("echo", "--demo-level"), ("probe", "--demo-flag"), ("probe", "--demo-level") }));
  }

  /// <summary>Test design 4: an identifier is accepted for the direction it was registered for only.</summary>
  [TestCase(IdentifierDirection.From, true, false)]
  [TestCase(IdentifierDirection.To, false, true)]
  [TestCase(IdentifierDirection.Both, true, true)]
  public void AnIdentifier_IsKnownForItsDirection(IdentifierDirection direction, bool from, bool to) {
    var (_, contributions, _) = ProbeCommand.Setup(new DemoIdentifiers(Owner, new ContributedIdentifier("demo-format", direction, "a demo format")));
    Assert.That(contributions.IsKnownIdentifier("demo-format", IdentifierDirection.From), Is.EqualTo(from));
    Assert.That(contributions.IsKnownIdentifier("demo-format", IdentifierDirection.To), Is.EqualTo(to));
    Assert.That(contributions.IsKnownIdentifier("Demo-Format", IdentifierDirection.From), Is.False, "matched exactly");
    Assert.That(contributions.IsKnownIdentifier("demo-format", IdentifierDirection.Both), Is.EqualTo(from && to));
  }

  /// <summary>Scenario 5: <c>probe</c> accepts a contributed identifier where it was registered and refuses it elsewhere with <c>PLY0006</c>.</summary>
  [Test]
  public void AContributedIdentifier_IsAcceptedForItsDirectionOnly() {
    object[] identifiers = [new DemoIdentifiers(Owner, new ContributedIdentifier("demo-format", IdentifierDirection.From, "a demo format"))];
    Assert.That(Invoke(identifiers, "probe", "input.bin", "--from", "demo-format").ExitCode, Is.Zero);
    var refused = Invoke(identifiers, "probe", "input.bin", "--to", "demo-format");
    Assert.That((refused.ExitCode, Lines(refused.Error)[0]), Is.EqualTo((2, "polypiler: error PLY0006: unknown language 'demo-format'")));
    Assert.That(Invoke([], "probe", "input.bin", "--from", "demo-format").ExitCode, Is.EqualTo(2), "nothing contributed");
  }

  /// <summary>Test design 5: the order of registration changes neither the help nor the canonical arguments.</summary>
  [Test]
  public void RegistrationOrder_ChangesNothing() {
    var first = new DemoContributor("A-ISSUE", ["probe"], Declared("--demo-b", OptionKind.Single));
    var second = new DemoContributor("B-ISSUE", ["probe"], Declared("--demo-a", OptionKind.Single));
    string[] line = ["probe", "input.txt", "--demo-b", "2", "--demo-a", "1"];
    var forward = Invoke([first, second], line);
    var backward = Invoke([second, first], line);
    Assert.That(forward.Canonical, Is.EqualTo(backward.Canonical).And.EqualTo("--opt balanced --demo-a 1 --demo-b 2"));
    var help = Invoke([first, second], "probe", "--help").Output;
    Assert.That(help, Is.EqualTo(Invoke([second, first], "probe", "--help").Output));
    Assert.That(help.IndexOf("Options from A-ISSUE:", StringComparison.Ordinal), Is.LessThan(help.IndexOf("Options from B-ISSUE:", StringComparison.Ordinal)));
  }

  /// <summary>Test design 6: an option that was not typed is not in the bag.</summary>
  [Test]
  public void AnAbsentOption_IsNotInTheBag() {
    var run = Invoke([DemoContributor.Level(Owner, "probe")], "probe", "input.txt");
    var bag = run.Probe.LastBag!;
    Assert.That(bag.TryGet(DemoContributor.ValueKey("--demo-level"), out var value), Is.False);
    Assert.That((value, bag.Count, bag.GetOrDefault(DemoContributor.ValueKey("--demo-level"), "fallback")), Is.EqualTo(((string?)null, 0, "fallback")));
    Assert.That(run.Canonical, Is.EqualTo("--opt balanced"));
  }

  /// <summary>Test design 7: no contributor, and a contributor without options, leave the command as it is.</summary>
  [Test]
  public void NoContribution_LeavesTheCommandAsItIs() {
    var none = Invoke([], "probe", "--help").Output;
    var empty = new DemoContributor(Owner, ["probe"]);
    Assert.That(Invoke([empty], "probe", "--help").Output, Is.EqualTo(none).And.Not.Contain("Options from"));
    var run = Invoke([empty], "probe", "input.txt", "--opt", "speed");
    Assert.That((run.ExitCode, run.Canonical, run.Probe.LastBag!.Count), Is.EqualTo((0, "--opt speed", 0)));
    Assert.That(ProbeCommand.Setup(empty).Contributions.Describe(), Is.Empty);
  }

  /// <summary>Test design 8: the shortest name and a name with digits and hyphens are accepted.</summary>
  [TestCase("--a")]
  [TestCase("--x86-64-v3")]
  public void AnOptionName_OfTheGrammar_IsAccepted(string name) {
    var run = Invoke([Demo(Declared(name, OptionKind.Flag))], "probe", "input.txt", name);
    Assert.That((run.ExitCode, run.Canonical), Is.EqualTo((0, "--opt balanced " + name)));
  }

  /// <summary>Test design 9: a name outside the grammar is refused at registration.</summary>
  [TestCase("--Demo")]
  [TestCase("--demo_level")]
  [TestCase("-d")]
  [TestCase("--demo-")]
  [TestCase("--")]
  public void AnInvalidOptionName_IsRefused(string name)
    => Assert.That(Refused(Demo(Declared(name, OptionKind.Flag))).Message,
      Is.EqualTo($"'{name}' of DEMO-ISSUE is not an option name: '--' followed by lower-case words of letters and digits joined by single hyphens"));

  /// <summary>A global option is no contributor's to add.</summary>
  [TestCase("--diagnostics")]
  [TestCase("--quiet")]
  [TestCase("--help")]
  [TestCase("--version")]
  public void AGlobalOptionName_IsRefused(string name)
    => Assert.That(Refused(Demo(Declared(name, OptionKind.Flag))).Message, Is.EqualTo($"option '{name}' of DEMO-ISSUE is a global option, which no contributor can add"));

  /// <summary>Test design 10: names that differ in their last character are recorded in ordinal order, whatever the order typed.</summary>
  [Test]
  public void NamesDifferingInTheLastCharacter_AreOrderedOrdinally() {
    var demo = Demo(Declared("--demo-b", OptionKind.Flag), Declared("--demo-a", OptionKind.Flag));
    Assert.That(Invoke([demo], "probe", "input.txt", "--demo-b", "--demo-a").Canonical, Is.EqualTo("--opt balanced --demo-a --demo-b"));
  }

  /// <summary>Test design 11 and scenario 4: a second registration of an option for one command names both owners.</summary>
  [Test]
  public void ADuplicateOption_NamesBothOwners() {
    var exception = Refused(DemoContributor.Level(Owner, "probe"), DemoContributor.Level("OTHER-ISSUE", "probe"));
    Assert.That(exception.Message, Is.EqualTo("option '--demo-level' of command 'probe' is already registered by DEMO-ISSUE (second registration: OTHER-ISSUE)"));
    var single = Refused(Demo(Declared("--demo-level", OptionKind.Single)), new DemoContributor("OTHER-ISSUE", ["probe"], Declared("--demo-level", OptionKind.Single)));
    Assert.That(single.Message, Is.EqualTo("option '--demo-level' of command 'probe' is already registered by DEMO-ISSUE (second registration: OTHER-ISSUE)"));
  }

  /// <summary>Test design 11: a refused contributor registers nothing; the same option on another command is no collision.</summary>
  [Test]
  public void ARefusedContributor_RegistersNothing() {
    var (_, contributions, _) = ProbeCommand.Setup(DemoContributor.Level(Owner, "probe"));
    Assert.Throws<InvalidOperationException>(() => contributions.Add(DemoContributor.Level("OTHER-ISSUE", "echo", "probe")));
    Assert.That(contributions.Describe().Select(option => option.Owner), Is.All.EqualTo(Owner));
    contributions.Add(DemoContributor.Level("OTHER-ISSUE", "echo", "echo"));
    Assert.That(contributions.Describe().Count(option => option.Command == "echo"), Is.EqualTo(2));
  }

  /// <summary>Test design 12: an option of the command itself cannot be contributed to it.</summary>
  [Test]
  public void AnOptionOfTheCommand_CannotBeContributed() {
    var exception = Refused(new DemoContributor(Owner, ["translate"], Declared("--output", OptionKind.Single)));
    Assert.That(exception.Message, Is.EqualTo("option '--output' of command 'translate' is an option of the command itself (second registration: DEMO-ISSUE)"));
  }

  /// <summary>A command that is not registered yet, such as <c>recover</c>, may be named; its options wait for it.</summary>
  [Test]
  public void ACommandNotRegistered_MayBeNamed() {
    var (_, contributions, _) = ProbeCommand.Setup(DemoContributor.Level(Owner, "recover"));
    Assert.That(contributions.Describe().Select(option => option.Command), Is.All.EqualTo("recover"));
  }

  /// <summary>Test design 13: an identifier registered twice for one direction names both owners; other directions do not collide.</summary>
  [Test]
  public void ADuplicateIdentifier_IsRefused() {
    var from = new DemoIdentifiers(Owner, new ContributedIdentifier("demo-format", IdentifierDirection.From, "a"));
    Assert.That(Refused(from, new DemoIdentifiers("OTHER-ISSUE", new ContributedIdentifier("demo-format", IdentifierDirection.From, "b"))).Message,
      Is.EqualTo("identifier 'demo-format' for --from is already registered by DEMO-ISSUE (second registration: OTHER-ISSUE)"));
    Assert.That(Refused(new DemoIdentifiers(Owner, new ContributedIdentifier("demo-format", IdentifierDirection.To, "a"), new ContributedIdentifier("demo-format", IdentifierDirection.Both, "b"))).Message,
      Is.EqualTo("identifier 'demo-format' for --to is already registered by DEMO-ISSUE (second registration: DEMO-ISSUE)"));
    Assert.That(Refused(new DemoIdentifiers(Owner, new ContributedIdentifier("demo-format", IdentifierDirection.Both, "a")), new DemoIdentifiers("OTHER-ISSUE", new ContributedIdentifier("demo-format", IdentifierDirection.Both, "b"))).Message,
      Is.EqualTo("identifier 'demo-format' for --from and --to is already registered by DEMO-ISSUE (second registration: OTHER-ISSUE)"));

    var (_, contributions, _) = ProbeCommand.Setup(from, new DemoIdentifiers("OTHER-ISSUE", new ContributedIdentifier("demo-format", IdentifierDirection.To, "b"), new ContributedIdentifier("bitmapping", IdentifierDirection.Both, "c")));
    Assert.That(contributions.Identifiers.Select(identifier => (identifier.Identifier.Name, identifier.Identifier.Direction, identifier.Owner)),
      Is.EqualTo(new[] { ("bitmapping", IdentifierDirection.Both, "OTHER-ISSUE"), ("demo-format", IdentifierDirection.From, Owner), ("demo-format", IdentifierDirection.To, "OTHER-ISSUE") }));
  }

  /// <summary>An identifier is a word of the grammar and has a direction.</summary>
  [TestCase("Demo", IdentifierDirection.From, "'Demo' of DEMO-ISSUE is not an identifier: lower-case words of letters and digits joined by single hyphens")]
  [TestCase("", IdentifierDirection.From, "'' of DEMO-ISSUE is not an identifier: lower-case words of letters and digits joined by single hyphens")]
  [TestCase("demo", (IdentifierDirection)0, "identifier 'demo' of DEMO-ISSUE has no direction")]
  [TestCase("demo", (IdentifierDirection)4, "identifier 'demo' of DEMO-ISSUE has no direction")]
  public void AnInvalidIdentifier_IsRefused(string name, IdentifierDirection direction, string message)
    => Assert.That(Refused(new DemoIdentifiers(Owner, new ContributedIdentifier(name, direction, "d"))).Message, Is.EqualTo(message));

  /// <summary>Test design 14 and scenario 3: an option nobody registered for the command is <c>PLY0003</c>.</summary>
  [Test]
  public void AnUnregisteredOption_IsRefusedByTheGrammar() {
    var run = Invoke([DemoContributor.Level(Owner, "probe")], "translate", "input.cs", "--from", "csharp", "--to", "javascript", "--demo-level", "3", "--output", "out");
    Assert.That((run.ExitCode, run.Output, run.Error), Is.EqualTo((2, "", "polypiler: error PLY0003: unknown option '--demo-level'" + Environment.NewLine)));
  }

  /// <summary>Test design 15 and scenario 6: a contributor's own diagnostic stops the command.</summary>
  [Test]
  public void AContributorsDiagnostic_StopsTheCommand() {
    var run = Invoke([DemoContributor.Level(Owner, "probe")], "probe", "input.txt", "--demo-level", "0");
    Assert.That((run.ExitCode, run.Output, Lines(run.Error)[0]), Is.EqualTo((2, "", "polypiler: error PLY9001: '0' is not a valid value for '--demo-level'")));
    Assert.That(run.Probe.Executions, Is.Zero);
  }

  /// <summary>A contributor that refuses without reporting why is a defect: an internal compiler error, and the command does not run.</summary>
  [Test]
  public void AContributorRefusingSilently_IsAnInternalError() {
    var run = Invoke([new DemoContributor(Owner, ["probe"]) { FailsSilently = true }], "probe", "input.txt");
    Assert.That((run.ExitCode, run.Probe.Executions), Is.EqualTo((70, 0)));
    Assert.That(run.Error, Does.Contain("error PLY0001: command 'probe' returned 2 without a diagnostic"));
  }

  /// <summary>Test design 17: each kind is written its own way in the canonical command line.</summary>
  [Test]
  public void EachKind_IsWrittenItsOwnWay() {
    var demo = Demo(Declared("--demo-flag", OptionKind.Flag), Declared("--demo-level", OptionKind.Single), Declared("--demo-tags", OptionKind.List, ListOrder.Sequence),
      Declared("--demo-with", OptionKind.RepeatOnly));
    var run = Invoke([demo], "probe", "input.txt", "--demo-with", "z,y", "--demo-tags", "b,a", "--demo-with", "x", "--demo-level", "7", "--demo-flag", "--demo-tags", "c");
    Assert.That((run.ExitCode, run.Error), Is.EqualTo((0, "")));
    Assert.That(run.Canonical, Is.EqualTo("--opt balanced --demo-flag --demo-level 7 --demo-tags b,a,c --demo-with x --demo-with z,y"));
    Assert.That(run.Probe.LastBag!.GetOrDefault(DemoContributor.ListKey("--demo-with"), []), Is.EqualTo(new[] { "z,y", "x" }), "a repeat-only value is never split");
    var help = Invoke([demo], "probe", "--help").Output;
    Assert.That(help, Does.Contain("  --demo-tags <item>[,<item>...]").And.Contain("  --demo-with <item> ").And.Not.Contain("--demo-with <item>["));
  }

  /// <summary>Test design 17: a single-valued or a flag contributed option appears at most once; a repeat-only one may repeat.</summary>
  [Test]
  public void ASingleValuedOption_GivenTwice_IsRefused() {
    var demo = Demo(Declared("--demo-level", OptionKind.Single), Declared("--demo-with", OptionKind.RepeatOnly));
    var run = Invoke([demo], "probe", "input.txt", "--demo-level", "1", "--demo-level", "2");
    Assert.That((run.ExitCode, run.Error), Is.EqualTo((2, "polypiler: error PLY0003: option '--demo-level' given more than once" + Environment.NewLine)));
    Assert.That(Invoke([demo], "probe", "input.txt", "--demo-with", "a", "--demo-with", "a").Canonical, Is.EqualTo("--opt balanced --demo-with a --demo-with a"));
  }

  /// <summary>Test design 18: a set collapses duplicates and sorts; a sequence keeps order and duplicates.</summary>
  [TestCase(ListOrder.Set, "a,b")]
  [TestCase(ListOrder.Sequence, "b,a,a")]
  public void AList_IsWrittenAsItsOrderSays(ListOrder order, string expected) {
    var run = Invoke([Demo(Declared("--demo-tags", OptionKind.List, order))], "probe", "input.txt", "--demo-tags", "b,a", "--demo-tags", "a");
    Assert.That(run.Canonical, Is.EqualTo("--opt balanced --demo-tags " + expected));
  }

  /// <summary>Test design 19: an option declared not recorded is read but not recorded.</summary>
  [Test]
  public void AnOptionNotRecorded_IsReadButNotRecorded() {
    var run = Invoke([Demo(Declared("--demo-trace", OptionKind.Flag, recorded: false))], "probe", "input.txt", "--demo-trace");
    Assert.That(run.Probe.LastBag!.GetOrDefault(DemoContributor.FlagKey("--demo-trace"), false), Is.True);
    Assert.That(run.Canonical, Is.EqualTo("--opt balanced"));
  }

  /// <summary>Test design 20: every spelling of one value is recorded in the normal spelling.</summary>
  [TestCase("255")]
  [TestCase("0xff")]
  [TestCase("0x00FF")]
  public void AValue_IsRecordedInItsNormalSpelling(string typed) {
    var run = Invoke([Demo(Declared("--demo-address", OptionKind.Single, spelling: DemoContributor.Address))], "probe", "input.txt", "--demo-address", typed);
    Assert.That(run.Canonical, Is.EqualTo("--opt balanced --demo-address 0x00FF"));
    Assert.That(run.Probe.LastBag!.GetOrDefault(DemoContributor.ValueKey("--demo-address"), ""), Is.EqualTo(typed), "the bag keeps the value as typed");
  }

  /// <summary>The normal spelling applies to every item of a list and to every value of a repeat-only option.</summary>
  [Test]
  public void TheNormalSpelling_AppliesToEveryValue() {
    var demo = Demo(Declared("--demo-tags", OptionKind.List, ListOrder.Set, spelling: DemoContributor.Address), Declared("--demo-with", OptionKind.RepeatOnly, spelling: DemoContributor.Address));
    var run = Invoke([demo], "probe", "input.txt", "--demo-tags", "0xff,255,1", "--demo-with", "16", "--demo-with", "0x2");
    Assert.That(run.Canonical, Is.EqualTo("--opt balanced --demo-tags 0x0001,0x00FF --demo-with 0x0002 --demo-with 0x0010"));
  }

  /// <summary>Test design 21: a single-valued option with a default is recorded whether typed or not.</summary>
  [TestCase("probe", "input.txt", "--demo-depth", "3")]
  [TestCase("probe", "input.txt")]
  public void AnOptionWithADefault_IsRecordedTypedOrNot(params string[] line)
    => Assert.That(Invoke([Demo(Declared("--demo-depth", OptionKind.Single, value: "3"))], line).Canonical, Is.EqualTo("--opt balanced --demo-depth 3"));

  /// <summary>Test design 22: a declaration that does not match what the contributor configures is refused naming the option and the owner.</summary>
  [Test]
  public void AFaultyDeclaration_IsRefused() {
    var undeclared = new DemoContributor(Owner, ["probe"]) { ConfigureInstead = (_, command) => command.Options.Add(new Option<bool>("--demo-flag")) };
    Assert.That(Refused(undeclared).Message, Is.EqualTo("option '--demo-flag' that DEMO-ISSUE configures on 'probe' has no declaration"));
    Assert.That(Refused(Demo(Declared("--demo-flag", OptionKind.Flag, ListOrder.Set))).Message,
      Is.EqualTo("option '--demo-flag' of DEMO-ISSUE is a Flag declared with the order Set; a list, and only a list, is a Set or a Sequence"));
    Assert.That(Refused(Demo(Declared("--demo-with", OptionKind.RepeatOnly, ListOrder.Sequence))).Message,
      Is.EqualTo("option '--demo-with' of DEMO-ISSUE is a RepeatOnly declared with the order Sequence; a list, and only a list, is a Set or a Sequence"));
    Assert.That(Refused(Demo(Declared("--demo-tags", OptionKind.List))).Message,
      Is.EqualTo("option '--demo-tags' of DEMO-ISSUE is a List declared with the order None; a list, and only a list, is a Set or a Sequence"));
  }

  /// <summary>Test design 22: what is configured must be what is declared, once and of the declared kind.</summary>
  [Test]
  public void TheConfiguration_MustMatchTheDeclaration() {
    var declared = Declared("--demo-level", OptionKind.Single);
    var unconfigured = new DemoContributor(Owner, ["probe"], declared) { ConfigureInstead = (_, _) => { } };
    Assert.That(Refused(unconfigured).Message, Is.EqualTo("option '--demo-level' that DEMO-ISSUE declares is not configured on 'probe'"));
    var twice = new DemoContributor(Owner, ["probe"], declared) {
      ConfigureInstead = (_, command) => {
        command.Options.Add(new Option<string>("--demo-level"));
        command.Options.Add(new Option<string>("--demo-level"));
      }
    };
    Assert.That(Refused(twice).Message, Is.EqualTo("option '--demo-level' is configured twice on 'probe' by DEMO-ISSUE"));
    var mistyped = new DemoContributor(Owner, ["probe"], declared) { ConfigureInstead = (_, command) => command.Options.Add(new Option<bool>("--demo-level")) };
    Assert.That(Refused(mistyped).Message, Is.EqualTo("option '--demo-level' of DEMO-ISSUE is declared a Single but configured as a Flag"));
    var repeat = new DemoContributor(Owner, ["probe"], Declared("--demo-with", OptionKind.RepeatOnly)) { ConfigureInstead = (_, command) => command.Options.Add(new Option<string>("--demo-with")) };
    Assert.That(Refused(repeat).Message, Is.EqualTo("option '--demo-with' of DEMO-ISSUE is declared a RepeatOnly but configured as a Single"));
    Assert.That(Refused(Demo(declared, declared)).Message, Is.EqualTo("option '--demo-level' is declared twice by DEMO-ISSUE"));
  }

  /// <summary>A contributor whose canonical words are not its options' is a defect, reported as an internal compiler error.</summary>
  [TestCase(new[] { "--demo-other", "1" }, "DEMO-ISSUE yields '--demo-other' as a canonical argument, which is no option it declares")]
  [TestCase(new[] { "--demo-level" }, "DEMO-ISSUE yields the option '--demo-level' without a value as a canonical argument")]
  public void FaultyCanonicalWords_AreAnInternalError(string[] words, string message) {
    var demo = new DemoContributor(Owner, ["probe"], Declared("--demo-level", OptionKind.Single)) { CanonicalInstead = words };
    var run = Invoke([demo], "probe", "input.txt");
    Assert.That((run.ExitCode, run.Probe.Executions), Is.EqualTo((70, 0)));
    Assert.That(run.Error, Does.Contain("InvalidOperationException: " + message));
  }

  /// <summary>The values of the public surface are checked.</summary>
  [Test]
  public void TheSurface_RefusesNull() {
    var (registry, contributions, _) = ProbeCommand.Setup();
    Assert.Throws<ArgumentNullException>(() => _ = new OptionContributions(null!));
    Assert.Throws<ArgumentNullException>(() => contributions.Add((ICommandOptionContributor)null!));
    Assert.Throws<ArgumentNullException>(() => contributions.Add((IIdentifierContributor)null!));
    Assert.Throws<ArgumentNullException>(() => contributions.ConfigureFor("probe", null!));
    Assert.Throws<ArgumentNullException>(() => Program.Run([], TextWriter.Null, TextWriter.Null, registry, null!, "."));
    Assert.Throws<ArgumentNullException>(() => Program.Run([], TextWriter.Null, TextWriter.Null, null!, "."));
    Assert.Throws<ArgumentException>(() => _ = new OptionKey<int>(""));
    var bag = new OptionBag();
    Assert.Throws<ArgumentNullException>(() => bag.Set(null!, 1));
    Assert.Throws<ArgumentNullException>(() => bag.TryGet<int>(null!, out _));
    Assert.That(ContributedOption.Identity("0xff"), Is.EqualTo("0xff"));
  }

  /// <summary>A key is the same key by name and type; a value set again replaces the first.</summary>
  [Test]
  public void TheBag_KeepsOneValuePerKey() {
    var bag = new OptionBag();
    bag.Set(new OptionKey<int>("level"), 1);
    bag.Set(new OptionKey<int>("level"), 2);
    bag.Set(new OptionKey<string>("level"), "text");
    Assert.That((bag.Count, bag.GetOrDefault(new OptionKey<int>("level"), 0), bag.GetOrDefault(new OptionKey<string>("level"), "")), Is.EqualTo((2, 2, "text")));
    Assert.That(new OptionKey<int>("level").Name, Is.EqualTo("level"));
  }

  /// <summary>Rule 5: <c>translate</c> records the contributed options after its own in the manifest.</summary>
  [Test]
  public void Translate_RecordsTheContributedOptionsInItsManifest() {
    var directory = Path.Combine(Path.GetTempPath(), "polypiler-contributions-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try {
      File.WriteAllText(Path.Combine(directory, "Snippet.cs"), PrototypeFixture.Wrap("public static int F(int a) => a + 1;"));
      var (registry, contributions, _) = ProbeCommand.Setup(DemoContributor.Level(Owner, "translate"));
      var exitCode = Program.Run(["translate", "Snippet.cs", "--from", "csharp", "--to", "javascript", "--output", "out", "--demo-level", "3"], TextWriter.Null, TextWriter.Null, registry, contributions, directory);
      Assert.That(exitCode, Is.Zero);
      var manifest = ManifestReader.Read(Path.Combine(directory, "out", Manifest.FileName), new DiagnosticBag())!;
      Assert.That(manifest.Command, Is.EqualTo(new[] { "translate", "../Snippet.cs", "--from", "csharp", "--to", "javascript", "--output", ".", "--demo-level", "3" }));
    } finally {
      Directory.Delete(directory, true);
    }
  }

  /// <summary>Test design 23: every contributed option of the PRD §10 invocations has exactly one row and one owner in the planned register.</summary>
  [Test]
  public void ThePlannedRegister_NamesOneOwnerPerContributedOption() {
    var owners = RegisteredOptionsDocument.PlannedOwners();
    string[] prd = ["--cpu", "--entry", "--memory-map", "--verify-emulator", "--strategy", "--native-provider", "--target-triple", "--llvm-version"];
    foreach (var option in prd)
      Assert.That(owners.TryGetValue(option, out var named) ? named : [], Has.Count.EqualTo(1), option);

    Assert.That(owners["--target-triple"].Single(), Is.EqualTo("#851"));
    Assert.That(owners["--llvm-version"].Single(), Is.EqualTo("#860"));
    Assert.That(owners["--strategy"].Single(), Is.EqualTo("#286"));
    Assert.That(owners, Does.Not.ContainKey("--from"), "an identifier row names no option");
    Assert.That(RegisteredOptionsDocument.Planned, Has.Count.EqualTo(16));
  }

  /// <summary>Test design 16: the committed <c>docs/cli/registered-options.md</c> is the document as generated.</summary>
  [Test]
  public void TheCommittedDocument_IsCurrent()
    => Assert.That(Staleness(Path.Combine(PrototypeFixture.RepositoryRoot, RegisteredOptionsDocument.DocumentPath)), Is.Null);

  /// <summary>Test design 16: an edited or missing copy is named as out of date.</summary>
  [Test]
  public void AStaleDocument_IsNamed() {
    var directory = Path.Combine(Path.GetTempPath(), "polypiler-registered-options-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try {
      var copy = Path.Combine(directory, "registered-options.md");
      File.WriteAllText(copy, RegisteredOptionsDocument.Markdown().Replace("#851", "#999", StringComparison.Ordinal));
      Assert.That(Staleness(copy), Is.EqualTo($"PLY0091: generated document out of date: '{copy}' differs"));
      File.Delete(copy);
      Assert.That(Staleness(copy), Is.EqualTo($"PLY0091: generated document out of date: '{copy}' is missing"));
    } finally {
      Directory.Delete(directory, true);
    }
  }

  /// <summary>The document lists what is registered: options with their declarations, identifiers with their directions.</summary>
  [Test]
  public void TheDocument_ListsWhatIsRegistered() {
    var empty = RegisteredOptionsDocument.Markdown();
    Assert.That(empty, Does.StartWith("# Registered options\n").And.Contain("No option is contributed yet.\n").And.EndWith("No identifier is contributed yet.\n").And.Not.Contain("\r"));
    Assert.That(empty, Does.Contain("| `--target-triple` | `translate` | #851 | target triple an `llvm` output is compiled, linked and executed for (PRD §10) |"));

    var demo = new DemoContributor(Owner, ["probe"], Declared("--demo-level", OptionKind.Single), Declared("--demo-tags", OptionKind.List, ListOrder.Set),
      Declared("--demo-trace", OptionKind.Flag, recorded: false));
    var (_, contributions, _) = ProbeCommand.Setup(demo, new DemoIdentifiers(Owner, new ContributedIdentifier("demo-format", IdentifierDirection.From, "a demo format")));
    var listed = RegisteredOptionsDocument.Markdown(contributions);
    Assert.That(listed, Does.Contain("| `probe` | `--demo-level` | Single | — | yes | DEMO-ISSUE |\n")
      .And.Contain("| `probe` | `--demo-tags` | List | Set | yes | DEMO-ISSUE |\n")
      .And.Contain("| `probe` | `--demo-trace` | Flag | — | no | DEMO-ISSUE |\n")
      .And.Contain("| `demo-format` | From | DEMO-ISSUE | a demo format |\n"));
  }

  /// <summary>What the regeneration check says about a copy of the document: <see langword="null"/> when it is current.</summary>
  private static string? Staleness(string path) {
    if (!File.Exists(path))
      return $"PLY0091: generated document out of date: '{path}' is missing";

    return File.ReadAllText(path).ReplaceLineEndings("\n") == RegisteredOptionsDocument.Markdown() ? null : $"PLY0091: generated document out of date: '{path}' differs";
  }

}
