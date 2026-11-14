using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Tests.Acceptance.Prototype;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Optimize;

/// <summary>
/// The steps of <c>tests/features/optimize/opt-target-ast.feature</c>: the golden outputs of the
/// backends, the parse-back of every generated file, and the target passes O0500 and O0501 on the
/// command line.
/// </summary>
[Binding, Scope(Feature = "Backends build a target syntax tree, optimize it and print it last")]
public sealed class TargetAstSteps(PrototypeScenario scenario) {

  private readonly ConcurrentBag<string> _problems = [];
  private readonly ConcurrentBag<TargetTree> _translated = [];
  private (string Path, string Text) _generated;

  private static IEnumerable<(string Source, GoldenVariant Variant)> Everything()
    => from source in GoldenOutputs.All from target in GoldenOutputs.Targets from variant in GoldenOutputs.Variants(target) select (source, variant);

  /// <summary>The golden files are there for every source and target.</summary>
  [Given("^the recorded golden outputs of every fixture, profile, module format and float mode$")]
  public void GoldenOutputsRecorded()
    => Assert.That(GoldenOutputs.All.SelectMany(source => GoldenOutputs.Targets.Select(target => GoldenOutputs.PathOf(source, target))).Where(path => !File.Exists(path)), Is.Empty);

  /// <summary>Translates every source in every variant again, in process, and keeps every section that differs from its golden one.</summary>
  [When("^each is translated again with \"--opt none\"$")]
  public void TranslatedAgain() => Parallel.ForEach(Everything(), item => {
    var now = GoldenOutputs.Translate(item.Source, item.Variant, scenario.Workspace.Resolve("."));
    if (now != GoldenOutputs.Recorded(item.Source, item.Variant.Target)[item.Variant.Name])
      this._problems.Add($"{item.Source}, {item.Variant}");
  });

  [Then("^every generated file is byte-identical to its golden output$")]
  public void ByteIdentical() => Assert.That(this._problems, Is.Empty);

  /// <summary>Builds the tree of every source in every variant with the target passes on and prints it.</summary>
  [When("^every fixture is translated for every profile of both backends$")]
  public void EveryFixtureTranslated() => Parallel.ForEach(Everything(), item => {
    if (TargetTrees.Translate(item.Source, item.Variant) is { } translated)
      this._translated.Add(translated);
  });

  [Then("^Roslyn at the profile's language version and Acornima at the profile's level parse each file without diagnostics$")]
  public void ParsedWithoutDiagnostics() {
    Assert.That(this._translated, Is.Not.Empty);
    Assert.That(this._translated.Select(TargetTrees.ParseBackProblem).Where(problem => problem is not null && problem != "the text parses to another tree"), Is.Empty);
  }

  [Then("^the parsed tree equals the printed target tree structurally$")]
  public void StructurallyEqual() => Assert.That(this._translated.Select(TargetTrees.ParseBackProblem).OfType<string>(), Is.Empty);

  [Then("^no generated file of any fixture declares a private helper that nothing references$")]
  public void NoUnreferencedHelper() {
    Assert.That(this._translated, Is.Not.Empty);
    Assert.That(this._translated.SelectMany(TargetTrees.UnreferencedHelpers), Is.Empty);
  }

  /// <summary>Translates the method of the scenario with the options of the step, which name the target.</summary>
  [When("^it is translated with " + StepText.Quoted + "$")]
  public void TranslatedWith(string options) {
    var arguments = CommandLine.Split(options).ToList();
    var to = arguments[arguments.IndexOf("--to") + 1];
    arguments.RemoveRange(arguments.IndexOf("--to"), 2);
    this._generated = scenario.Translate(scenario.Source, "csharp", to, [.. arguments]);
  }

  /// <summary>The output lacks the text, and node computes the value with it.</summary>
  [Then("^the output contains no " + StepText.Quoted + @" and (F\(.*\)) returns (\d+)$")]
  public void ContainsNoAndReturns(string text, string call, string value) {
    Assert.That(this._generated.Text, Does.Not.Contain(text));
    var parsed = CallText.OfSource(call, File.ReadAllText(scenario.Workspace.Resolve(scenario.Source)));
    var outcome = Toolchain.NodeOutcomes(Path.GetDirectoryName(this._generated.Path)!, Path.GetFileName(this._generated.Path), [parsed], false, "plain").Single();
    Assert.That(outcome, Is.EqualTo(value));
  }

  [Then("^the output declares " + StepText.Quoted + " and calls it$")]
  public void DeclaresAndCalls(string helper) => Assert.That(this._generated.Text, Does.Contain($"function {helper}(").And.Contain($"return {helper}("));

  /// <summary>Runs the translate command on a method of its own with the options of the step.</summary>
  [When("^I run the translate command with " + StepText.Quoted + "$")]
  public void TranslateCommandWith(string options) {
    scenario.Snippet("public static int F(int x) => x;");
    scenario.Polypiler.Run(["translate", scenario.Source, "--from", "csharp", "--to", "javascript", "--output", "out", .. CommandLine.Split(options)]);
  }

  [Then(@"^the exit code is (\d+) and the output contains " + StepText.Quoted + "$")]
  public void Refused(int exitCode, string code) {
    var result = scenario.Log.Last;
    Assert.That(result.ExitCode, Is.EqualTo(exitCode), result.Output);
    Assert.That(result.Output, Does.Contain(code));
  }

}
