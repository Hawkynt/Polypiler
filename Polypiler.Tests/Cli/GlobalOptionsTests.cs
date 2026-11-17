using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Core.Optimize;
using Polypiler.Core.Options;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Cli.Support;

namespace Polypiler.Tests.Cli;

/// <summary>
/// The pipeline options <c>--opt</c>, <c>--disable-opt</c>, <c>--assume</c> and <c>--strict</c>
/// through the test-only command <c>options</c>, in process (issue #179, test design 1-27).
/// </summary>
[TestFixture]
public sealed class GlobalOptionsTests {

  private sealed record Run(int ExitCode, string Output, string Error, OptionsCommand Command) {

    public string[] ErrorLines => this.Error.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

  }

  private static Run Invoke(params string[] args) => InvokeWith([], args);

  private static Run InvokeWith(DiagnosticDescriptor[] reports, params string[] args) {
    var (registry, command) = OptionsCommand.Registry(reports);
    using var output = new StringWriter();
    using var error = new StringWriter();
    var exitCode = Program.Run(args, output, error, registry, PrototypeFixture.RepositoryRoot);
    return new Run(exitCode, output.ToString().TrimEnd('\r', '\n'), error.ToString(), command);
  }

  private static void AssertRefused(Run run, string code, string message) {
    Assert.That(run.ExitCode, Is.EqualTo((int)ExitCode.UsageError), run.Error);
    Assert.That(run.Output, Is.Empty);
    Assert.That(run.ErrorLines[0], Is.EqualTo($"polypiler: error {code}: {message}"));
  }

  /// <summary>Test design 1 and the scenario "Defaults".</summary>
  [Test]
  public void Defaults() {
    var run = Invoke("options");
    Assert.That((run.ExitCode, run.Output, run.Error), Is.EqualTo((0, "--opt balanced", "")));
    var options = run.Command.Last!;
    Assert.That((options.Opt, options.DisabledOptimizations.Count, options.Assumptions.Count, options.Strict), Is.EqualTo((OptimizationLevel.Balanced, 0, 0, false)));
  }

  /// <summary>Test design 2: each level.</summary>
  [TestCase("none", OptimizationLevel.None)]
  [TestCase("balanced", OptimizationLevel.Balanced)]
  [TestCase("speed", OptimizationLevel.Speed)]
  [TestCase("size", OptimizationLevel.Size)]
  [TestCase("memory", OptimizationLevel.Memory)]
  public void EachLevel_IsRead(string name, OptimizationLevel level) {
    var run = Invoke("options", "--opt", name);
    Assert.That((run.ExitCode, run.Output), Is.EqualTo((0, "--opt " + name)));
    Assert.That(run.Command.Last!.Opt, Is.EqualTo(level));
    Assert.That(GlobalOptionSet.LevelName(level), Is.EqualTo(name));
    Assert.That(GlobalOptionSet.LevelOf(name), Is.EqualTo(level));
  }

  /// <summary>The default level written as the old name is the default.</summary>
  [Test]
  public void TheDefaultLevel_IsBalanced() => Assert.That(GlobalOptionSet.LevelName(OptimizationLevel.Default), Is.EqualTo("balanced"));

  [Test]
  public void AValueThatIsNoLevel_HasNoName() {
    Assert.That(() => GlobalOptionSet.LevelName((OptimizationLevel)99), Throws.TypeOf<ArgumentOutOfRangeException>());
    Assert.That(GlobalOptionSet.LevelOf(null), Is.Null);
  }

  /// <summary>Test design 16 and the scenario "An unknown level is refused".</summary>
  [TestCase("fastest"), TestCase("Speed"), TestCase("default"), TestCase("")]
  public void AnUnknownLevel_IsPLY0012(string level)
    => AssertRefused(Invoke("options", "--opt", level), "PLY0012", $"'{level}' is not an optimization level; expected one of: none, balanced, speed, size, memory");

  /// <summary>Test design 17.</summary>
  [Test]
  public void OptTwice_IsPLY0003() => AssertRefused(Invoke("options", "--opt", "speed", "--opt", "size"), "PLY0003", "option '--opt' given more than once");

  /// <summary>Test design 10: the form of an ID, on and just beside it.</summary>
  [TestCase("O0001", null)]
  [TestCase("O9999", "unknown optimization 'O9999'")]
  [TestCase("O001", "'O001' is not an optimization ID (expected O followed by four digits)")]
  [TestCase("O00001", "'O00001' is not an optimization ID (expected O followed by four digits)")]
  [TestCase("o0001", "'o0001' is not an optimization ID (expected O followed by four digits)")]
  public void OptimizationIds_AreChecked(string id, string? message) {
    var run = Invoke("options", "--disable-opt", id);
    if (message is null)
      Assert.That((run.ExitCode, run.Output), Is.EqualTo((0, "--opt balanced --disable-opt O0001")));
    else
      AssertRefused(run, "PLY0008", message);
  }

  /// <summary>Every bad item is named, in order.</summary>
  [Test]
  public void EveryBadId_IsReported() {
    var run = Invoke("options", "--disable-opt", "O0002,o1,O0001");
    Assert.That(run.ErrorLines.Take(2), Is.EqualTo(new[] {
      "polypiler: error PLY0008: unknown optimization 'O0002'",
      "polypiler: error PLY0008: 'o1' is not an optimization ID (expected O followed by four digits)"
    }));
  }

  /// <summary>Test design 24: an empty item is refused by the foundation before any ID is checked.</summary>
  [TestCase("O0001,,O0100"), TestCase("O0001,"), TestCase("O0001,,O0002")]
  public void AnEmptyItem_IsPLY0003(string list) {
    var run = Invoke("options", "--disable-opt", list);
    Assert.That(run.Error, Is.EqualTo("polypiler: error PLY0003: option '--disable-opt' has an empty list element" + Environment.NewLine));
    Assert.That(run.ExitCode, Is.EqualTo((int)ExitCode.UsageError));
  }

  [Test]
  public void AnEmptyAttachedValue_IsPLY0003()
    => Assert.That(Invoke("options", "--disable-opt=").Error, Does.StartWith("polypiler: error PLY0003: option '--disable-opt' has an empty list element"));

  /// <summary>Test design 20.</summary>
  [Test]
  public void DisableOptWithOptNone_IsAccepted()
    => Assert.That(Invoke("options", "--opt", "none", "--disable-opt", "O0001").Output, Is.EqualTo("--opt none --disable-opt O0001"));

  /// <summary>Test design 25 and the scenario of one canonical form.</summary>
  [TestCase("--disable-opt", "O0001,O0100")]
  [TestCase("--disable-opt", "O0100", "--disable-opt", "O0001")]
  [TestCase("--disable-opt", "O0100,O0001", "--disable-opt", "O0100")]
  public void DisableOptSpellings_AreOneSet(params string[] options)
    => Assert.That(Invoke(["options", .. options]).Output, Is.EqualTo("--opt balanced --disable-opt O0001,O0100"));

  [Test]
  public void EquivalentCommandLines_HaveOneCanonicalForm()
    => Assert.That(Invoke("options", "--disable-opt", "O0100,O0001", "--disable-opt", "O0100", "--opt", "speed").Output, Is.EqualTo("--opt speed --disable-opt O0001,O0100"));

  /// <summary>Test design 27.</summary>
  [Test]
  public void TheDefaultLevel_TypedOrOmitted_RendersTheSame()
    => Assert.That(Invoke("options", "--opt", "balanced").Output, Is.EqualTo(Invoke("options").Output));

  /// <summary>The scenario "An assumption is accepted, normalized and reported".</summary>
  [Test]
  public void AnAssumption_IsNormalizedAndReported() {
    var run = Invoke("options", "--assume", "Sum.n in [0x1,31]");
    Assert.That((run.ExitCode, run.Output), Is.EqualTo((0, "--opt balanced --assume \"Sum.n in [1, 31]\"")));
    Assert.That(run.ErrorLines[0], Is.EqualTo("polypiler: warning PLY0009: assumption 'Sum.n in [1, 31]' is trusted without proof"));
  }

  /// <summary>Test design 11: zero, one and three facts give as many warnings, in command-line order; the rendering orders them by text.</summary>
  [TestCase(0), TestCase(1), TestCase(3)]
  public void EveryFact_IsReportedOnceInOrder(int count) {
    string[] facts = ["z != null", "a is u8", "m noalias n"];
    var run = Invoke(["options", .. facts.Take(count).SelectMany(fact => new[] { "--assume", fact })]);
    Assert.That(run.ExitCode, Is.Zero);
    Assert.That(run.ErrorLines.Where(line => line.Contains("PLY0009", StringComparison.Ordinal)),
      Is.EqualTo(facts.Take(count).Select(fact => $"polypiler: warning PLY0009: assumption '{fact}' is trusted without proof")));
    Assert.That(run.Output, Is.EqualTo("--opt balanced" + string.Concat(facts.Take(count).Order(StringComparer.Ordinal).Select(fact => $" --assume \"{fact}\""))));
    Assert.That(run.Command.Last!.Assumptions.Select(fact => fact.Text), Is.EqualTo(facts.Take(count)));
  }

  /// <summary>Test design 26: a comma inside one --assume is not split.</summary>
  [Test]
  public void ACommaInsideAnAssumption_IsNotSplit() {
    var run = Invoke("options", "--assume", "a in [0, 9]");
    Assert.That(run.Command.Last!.Assumptions.Single().Kind, Is.EqualTo(AssumptionKind.Range));
  }

  /// <summary>Test design 12, 8 and the scenario "An inverted range is refused".</summary>
  [TestCase("", "empty assumption")]
  [TestCase("Sum.n in [32, 31]", "lower bound 32 exceeds upper bound 31")]
  [TestCase("Sum.n 5", "expected 'in', '!=', 'is' or 'noalias' after the path")]
  [TestCase("Sum.n: int[0, 9]", "contract assumptions are not available in this build")]
  public void AnInvalidAssumption_IsPLY0007(string text, string reason)
    => AssertRefused(Invoke("options", "--assume", text), "PLY0007", $"'{text}' is not a valid assumption: {reason}");

  [Test]
  public void AnEmptyAttachedAssumption_IsPLY0007()
    => AssertRefused(Invoke("options", "--assume="), "PLY0007", "'' is not a valid assumption: empty assumption");

  /// <summary>A refused command line reports no PLY0009 for the facts it did accept.</summary>
  [Test]
  public void AnError_SuppressesTheWarnings() {
    var run = Invoke("options", "--assume", "a != null", "--opt", "fast");
    Assert.That(run.Error, Does.Not.Contain("PLY0009"));
    Assert.That(run.Command.Last, Is.Null);
  }

  /// <summary>Test design 18 and the scenario "Strict mode refuses an unproven assumption".</summary>
  [Test]
  public void Strict_RefusesAnAssumption() {
    var run = Invoke("options", "--strict", "--assume", "Sum.n in [1, 31]");
    Assert.That(run.ExitCode, Is.EqualTo((int)ExitCode.UnsupportedConstruct));
    Assert.That(run.ErrorLines[0], Is.EqualTo("polypiler: error PLY0010: strict mode: PLY0009 assumption 'Sum.n in [1, 31]' is trusted without proof"));
    Assert.That(run.Error, Does.Not.Contain("warning PLY0009"));
    Assert.That(run.Output, Is.Empty);
  }

  [Test]
  public void Strict_WithoutAssumptions_IsRendered() {
    var run = Invoke("options", "--strict");
    Assert.That((run.ExitCode, run.Output), Is.EqualTo((0, "--opt balanced --strict")));
    Assert.That(run.Command.Last!.Strict, Is.True);
  }

  /// <summary>Test design 19: a warning not marked strict stays a warning under --strict.</summary>
  [Test]
  public void Strict_LeavesAnotherWarningAWarning() {
    var run = InvokeWith([TestCommands.TestWarning], "options", "--strict");
    Assert.That(run.ExitCode, Is.Zero);
    Assert.That(run.ErrorLines[0], Is.EqualTo("polypiler: warning PLY0098: test warning"));
  }

  /// <summary>Test design 22: each of the four before the command name.</summary>
  [TestCase("--opt", "speed")]
  [TestCase("--disable-opt", "O0001")]
  [TestCase("--assume", "a != null")]
  [TestCase("--strict")]
  public void BeforeTheCommandName_IsAnUnknownOption(params string[] option) {
    var run = Invoke([.. option, "options"]);
    Assert.That((run.ExitCode, run.Output, run.Error), Is.EqualTo(((int)ExitCode.UsageError, "", $"polypiler: error PLY0003: unknown option '{option[0]}'" + Environment.NewLine)));
  }

  /// <summary>Test design 23: on a command that does not attach them.</summary>
  [TestCase("--opt", "speed"), TestCase("--strict"), TestCase("--assume", "a != null"), TestCase("--disable-opt", "O0001")]
  public void OnACommandWithoutThem_IsAnUnknownOption(params string[] option)
    => AssertRefused(Invoke(["echo", "tests/fixtures/cli/foundation/input.txt", .. option]), "PLY0003", $"unknown option '{option[0]}'");

  /// <summary>Test design 21: every order of four options gives one rendering.</summary>
  [Test]
  public void EveryPermutation_RendersTheSame() {
    string[][] options = [["--opt", "speed"], ["--disable-opt", "O0100,O0001"], ["--assume", "b != null"], ["--assume", "a in [0x0, 9]"]];
    var renderings = Permutations(options).Select(order => Invoke(["options", .. order.SelectMany(option => option)]).Output).ToList();
    Assert.That(renderings, Has.Count.EqualTo(24));
    Assert.That(renderings.Distinct().Single(), Is.EqualTo("--opt speed --disable-opt O0001,O0100 --assume \"a in [0, 9]\" --assume \"b != null\""));
  }

  private static IEnumerable<IReadOnlyList<T>> Permutations<T>(IReadOnlyList<T> items)
    => items.Count == 0 ? [[]] : items.SelectMany((item, index) => Permutations([.. items.Where((_, other) => other != index)]).Select(rest => (IReadOnlyList<T>)[item, .. rest]));

  /// <summary>The options the set attaches, in the order of the canonical command line.</summary>
  [Test]
  public void TheOptions_AreAttachedInOrder() {
    var command = new Command("x");
    GlobalOptionSet.AddTo(command);
    Assert.That(command.Options.Select(option => option.Name), Is.EqualTo(GlobalOptionSet.Names));
  }

  /// <summary>A repeat-only option keeps a value that starts with a comma or is empty, once per occurrence.</summary>
  [Test]
  public void RepeatOnly_KeepsEveryValueWhole() {
    var run = Invoke("options", "--assume", "a != null", "--assume", "b in [1,2]");
    Assert.That(run.Command.Last!.Assumptions.Select(fact => fact.Text), Is.EqualTo(new[] { "a != null", "b in [1, 2]" }));
  }

  /// <summary>The help shows a repeat-only option with one value.</summary>
  [Test]
  public void Help_ShowsTheOptions() {
    var run = Invoke("options", "--help");
    Assert.That(run.Output, Does.Contain("--assume <fact>").And.Contain("--disable-opt <O####>[,<O####>...]").And.Contain("--opt <level>").And.Contain("--strict"));
  }

  [Test]
  public void Arguments_AreChecked() {
    Assert.That(() => GlobalOptionSet.AddTo(null!), Throws.ArgumentNullException);
    Assert.That(() => OptionRules.RepeatOnly(null!), Throws.ArgumentNullException);
    var parse = new Command("x").Parse([]);
    using var writer = new StringWriter();
    var context = new CliContext(writer, writer, "", DiagnosticsFormat.Text, false, ".");
    Assert.That(() => GlobalOptionSet.Read(null!, context, []), Throws.ArgumentNullException);
    Assert.That(() => GlobalOptionSet.Read(parse, null!, []), Throws.ArgumentNullException);
    Assert.That(() => GlobalOptionSet.Read(parse, context, null!), Throws.ArgumentNullException);
  }

  /// <summary>The sink of --strict promotes a strict warning and nothing else.</summary>
  [Test]
  public void StrictSink_PromotesOnlyStrictWarnings() {
    var bag = new DiagnosticBag();
    var sink = new StrictSink(bag);
    var trusted = Diagnostic.Create(PipelineOptionDiagnostics.TrustedAssumption, new SourceRange("a.cs", 2, 3, 2, 9), "a != null");
    sink.Report(trusted);
    sink.Report(Diagnostic.Create(TestCommands.TestWarning, null));
    sink.Report(Diagnostic.Create(TestCommands.TestError, null));
    sink.Report(trusted with { Severity = DiagnosticSeverity.Info });
    Assert.That(bag.Items.Select(item => (item.Code, item.Severity)), Is.EqualTo(new[] {
      ("PLY0010", DiagnosticSeverity.Error), ("PLY0098", DiagnosticSeverity.Warning), ("PLY0098", DiagnosticSeverity.Error), ("PLY0009", DiagnosticSeverity.Info)
    }));
    Assert.That((bag.Items[0].Message, bag.Items[0].File, bag.Items[0].Line, bag.Items[0].Column, bag.Items[0].ExitCode, bag.Items[0].Rule),
      Is.EqualTo(("strict mode: PLY0009 assumption 'a != null' is trusted without proof", "a.cs", 2, 3, (ExitCode?)ExitCode.UnsupportedConstruct, PipelineOptionDiagnostics.Rule)));
    Assert.That(bag.ExitCode, Is.EqualTo(ExitCode.UnsupportedConstruct));
    Assert.That(() => new StrictSink(null!), Throws.ArgumentNullException);
    Assert.That(() => StrictSink.Promote(null!), Throws.ArgumentNullException);
  }

  /// <summary>The codes --strict promotes are those the registry marks strict.</summary>
  [Test]
  public void StrictCodes_AreThoseOfTheRegistry() {
    var registry = Polypiler.Tooling.Diagnostics.DiagnosticRegistryValidator.Validate(Path.Combine(PrototypeFixture.RepositoryRoot, Polypiler.Tooling.Diagnostics.DiagnosticRegistry.RepositoryPath));
    Assert.That(StrictSink.StrictCodes, Is.EquivalentTo(registry.Codes.Where(entry => entry.Strict).Select(entry => entry.Code)));
  }

  /// <summary>The record itself: the canonical arguments of the default, and a level outside the table.</summary>
  [Test]
  public void TheDefault_RendersItsLevel() {
    Assert.That(GlobalOptions.Default.CanonicalArguments(), Is.EqualTo(new[] { "--opt", "balanced" }));
    Assert.That(() => (GlobalOptions.Default with { Opt = (OptimizationLevel)42 }).ToCanonicalArguments(), Throws.TypeOf<ArgumentOutOfRangeException>());
  }

}
