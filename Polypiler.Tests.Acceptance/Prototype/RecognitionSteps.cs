using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Optimize;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The steps of <c>tests/features/prototype/recognition.feature</c>: every C# output built for its
/// framework and for this runtime, every JavaScript output parsed at its level and run by node.
/// </summary>
[Binding, Scope(Feature = "Spelled-out bit idioms become the target's own operations")]
public sealed class RecognitionSteps(PrototypeScenario scenario) {

  private static readonly bool[] _overflowChecking = [false, true];

  private (string Path, string Text) _generated;
  private string[] _options = [];

  /// <summary>The lines of the last command's standard error that report an applied rule.</summary>
  private List<string> Applied => [.. scenario.Log.Last.Stderr.Split('\n').Where(line => line.Contains(": opt ", StringComparison.Ordinal))];

  private static string Expected(string value)
    => value.StartsWith("0x", StringComparison.Ordinal) ? ulong.Parse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) : value;

  private PrototypeCall Call(string text) => CallText.OfSource(text, File.ReadAllText(scenario.Workspace.Resolve(scenario.Source)));

  /// <summary>Translates the source with the options of the step, which name the target.</summary>
  [When("^it is translated with " + StepText.Quoted + "$")]
  public void TranslatedWith(string options) {
    var arguments = CommandLine.Split(options).ToList();
    var to = arguments[arguments.IndexOf("--to") + 1];
    arguments.RemoveRange(arguments.IndexOf("--to"), 2);
    this._options = [.. arguments];
    this._generated = scenario.Translate(scenario.Source, "csharp", to, this._options);
  }

  /// <summary>The output calls the library member and no longer has the shift.</summary>
  [Then("^the output calls " + StepText.Quoted + " and contains no " + StepText.Quoted + "$")]
  public void CallsAndContainsNo(string member, string text) => Assert.That(this._generated.Text, Does.Contain(member).And.Not.Contain(text));

  /// <summary>The output calls the library member.</summary>
  [Then("^the output calls " + StepText.Quoted + "$")]
  public void Calls(string member) => Assert.That(this._generated.Text, Does.Contain(member));

  /// <summary>The output still has the shift and does not call the library member.</summary>
  [Then("^the output contains " + StepText.Quoted + " and no " + StepText.Quoted + "$")]
  public void ContainsAndNo(string text, string member) => Assert.That(this._generated.Text, Does.Contain(text).And.Not.Contain(member));

  /// <summary>The C# output, compiled for this runtime, computes the value of the step.</summary>
  [Then(@"^(F\(.*\)) returns (0x[0-9A-Fa-f]+|-?\d+)$")]
  public void Returns(string text, string value) {
    var outcome = GeneratedCSharp.Compile(this._generated.Text, false).Outcome(this.Call(text));
    var expected = Expected(value);
    Assert.That(outcome == expected || outcome.StartsWith(expected + " | ", StringComparison.Ordinal), Is.True, outcome);
  }

  /// <summary>Standard error reports each rule at least as often as the step says.</summary>
  [Then(@"^standard error lists (O\d{4}) at least (\d+) times and (O\d{4}) at least once$")]
  public void ListsTheRules(string first, int times, string second) {
    Assert.That(this.Applied.Count(line => line.Contains($" {first} ", StringComparison.Ordinal)), Is.GreaterThanOrEqualTo(times));
    Assert.That(this.Applied.Count(line => line.Contains($" {second} ", StringComparison.Ordinal)), Is.GreaterThanOrEqualTo(1));
  }

  /// <summary>Node computes the published hash with the rules and, translated again with the option, without any rule applied.</summary>
  [Then("^Hash\\(empty, 0\\) returns 0x([0-9A-Fa-f]+) with and without " + StepText.Quoted + "$")]
  public void HashWithAndWithout(string hex, string option) {
    var call = new PrototypeCall("Hash", [Array.Empty<byte>(), 0u]);
    var expected = Expected("0x" + hex) + " | []";
    Assert.That(Toolchain.NodeOutcomes(Path.GetDirectoryName(this._generated.Path)!, Path.GetFileName(this._generated.Path), [call]).Single(), Is.EqualTo(expected), "default");
    var without = scenario.Translate(scenario.Source, "csharp", "javascript", [.. this._options, .. CommandLine.Split(option)]);
    Assert.That(this.Applied, Is.Empty);
    Assert.That(Toolchain.NodeOutcomes(Path.GetDirectoryName(without.Path)!, Path.GetFileName(without.Path), [call]).Single(), Is.EqualTo(expected), option);
  }

  /// <summary>The optimized IR has the operation of the step and not the other.</summary>
  [Then("^the optimized IR contains " + StepText.Quoted + " and no " + StepText.Quoted + "$")]
  public void OptimizedIrContains(string operation, string other) {
    var ir = IrText.Of(Optimizer.Run(PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap(scenario.Text)), new OptimizerOptions()).Module);
    Assert.That(ir, Does.Contain(operation).And.Not.Contain(other));
  }

  /// <summary>Every profile of both backends computes the value of the step, the C# ones with overflow checking off and on.</summary>
  [Then(@"^(F\(.*\)) returns (-?\d+) in every profile$")]
  public void ReturnsInEveryProfile(string text, string expected) {
    var call = this.Call(text);
    Sweep.Each(TargetCase.All, target => {
      var outcomes = new Dictionary<string, string>();
      var (file, generated) = scenario.Translate(scenario.Source, "csharp", target.Language, target.Arguments);
      if (target.CSharp is { } profile) {
        Assert.That(GeneratedCSharp.ComplaintsFor(generated, profile.Name, profile.LanguageVersionName), Is.Empty, $"{profile}:\n{generated}");
        foreach (var checking in _overflowChecking)
          outcomes[$"{profile.Name}, CheckOverflow {checking}"] = GeneratedCSharp.Compile(generated, checking).Outcome(call);
      } else {
        var script = target.JavaScript!;
        Assert.That(GeneratedJavaScript.Problems(generated, script.Parsed), Is.Empty, script.ToString());
        outcomes[script.ToString()] = Toolchain.NodeOutcomes(Path.GetDirectoryName(file)!, Path.GetFileName(file), [call], false, script.Mode, script.Global).Single();
      }

      Assert.That(outcomes.Where(run => run.Value != expected).Select(run => $"{run.Key}: {run.Value}"), Is.Empty);
    });
  }

  /// <summary>With the rules explained, none applies, and the output keeps both shifts.</summary>
  [Then("^no rule is applied and the output contains " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void NoRuleIsApplied(string first, string second) {
    this._generated = scenario.Translate(scenario.Source, "csharp", "csharp", "--profile", "net10.0", "--explain-opt");
    Assert.That(scenario.Log.Last.Output, Does.Not.Contain(": opt "));
    Assert.That(this._generated.Text, Does.Contain(first).And.Contain(second));
  }

  /// <summary>Runs the translate command on a method of its own with the options of the step.</summary>
  [When("^I run the translate command with " + StepText.Quoted + "$")]
  public void TranslateCommandWith(string options) {
    scenario.Snippet("public static int F(int x) => x;");
    scenario.Polypiler.Run(["translate", scenario.Source, "--from", "csharp", "--to", "javascript", "--output", "out", .. CommandLine.Split(options)]);
  }

  /// <summary>The command was refused as a usage error.</summary>
  [Then(@"^the exit code is (\d+) and the output contains " + StepText.Quoted + "$")]
  public void Refused(int exitCode, string code) {
    var result = scenario.Log.Last;
    Assert.That(result.ExitCode, Is.EqualTo(exitCode), result.Output);
    Assert.That(result.Output, Does.Contain(code));
  }

}
