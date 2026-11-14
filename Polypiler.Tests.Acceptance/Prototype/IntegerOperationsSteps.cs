using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The steps of <c>tests/features/prototype/integer-operations.feature</c>: every C# output is built by
/// the C# compiler for its profile's framework and for this runtime, every JavaScript output is parsed
/// at its level and run by node, and the interpreter runs the imported IR.
/// </summary>
[Binding, Scope(Feature = "The complete integer and bit operation set")]
public sealed class IntegerOperationsSteps(PrototypeScenario scenario) {

  private static readonly bool[] _overflowChecking = [false, true];

  private readonly ConcurrentDictionary<TargetCase, (string Path, string Text)> _outputs = new();
  private readonly Dictionary<string, string> _named = [];
  private string _from = "";

  private static string Language(string path) => Path.GetExtension(path) == ".cs" ? "csharp" : "javascript";

  /// <summary>Translates the source for one target.</summary>
  private (string Path, string Text) Translate(TargetCase target)
    => scenario.Translate(scenario.Source, Language(scenario.Source), target.Language, target.Arguments);

  /// <summary>What one C# or JavaScript profile makes of the source for the calls: the outcomes by build.</summary>
  private Dictionary<string, IReadOnlyList<string>> OneProfile(TargetCase target, IReadOnlyList<PrototypeCall> calls) {
    var outcomes = new Dictionary<string, IReadOnlyList<string>>();
    var (file, text) = this.Translate(target);
    if (target.CSharp is { } profile) {
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, $"{profile}:\n{text}");
      foreach (var checking in _overflowChecking) {
        var build = GeneratedCSharp.Compile(text, checking);
        outcomes[$"{profile.Name}, CheckOverflow {checking}"] = [.. calls.Select(build.Outcome)];
      }
    } else {
      var script = target.JavaScript!;
      Assert.That(GeneratedJavaScript.Problems(text, script.Parsed), Is.Empty, script.ToString());
      outcomes[script.ToString()] = Toolchain.NodeOutcomes(Path.GetDirectoryName(file)!, Path.GetFileName(file), calls, false, script.Mode, script.Global);
    }

    return outcomes;
  }

  /// <summary>Every target of every profile has the outcomes the step expects.</summary>
  private void EveryProfileReturns(IReadOnlyList<PrototypeCall> calls, IReadOnlyList<string> expected)
    => Sweep.Each(TargetCase.All, target => {
      var outcomes = this.OneProfile(target, calls);
      Assert.That(outcomes.Where(run => !run.Value.SequenceEqual(expected)).Select(run => $"{run.Key}: {string.Join(", ", run.Value)}"), Is.Empty);
    });

  /// <summary>Translates the fixture for every profile of the target language.</summary>
  [When(@"^it is translated from (csharp|javascript) to (csharp|javascript) for every profile of \2$")]
  public void TranslatedForEveryProfile(string from, string to) {
    Assert.That(Language(scenario.Source), Is.EqualTo(from));
    this._from = from;
    Sweep.Each(to == "csharp" ? TargetCase.CSharpProfiles : TargetCase.JavaScriptProfiles, target => this._outputs[target] = this.Translate(target));
  }

  /// <summary>Every C# output compiles against its profile's reference assemblies, every JavaScript output parses at its level.</summary>
  [Then("^every output compiles or parses for its profile$")]
  public void EveryOutputCompilesOrParses() {
    Assert.That(this._outputs, Is.Not.Empty);
    Sweep.Each([.. this._outputs.Keys], target => {
      var text = this._outputs[target].Text;
      if (target.CSharp is { } profile)
        Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, $"{profile}:\n{text}");
      else
        Assert.That(GeneratedJavaScript.Problems(text, target.JavaScript!.Parsed), Is.Empty, target.ToString());
    });
  }

  /// <summary>The call has the published result in every output, with overflow checking off and on for C#.</summary>
  [Then(@"^(\w+\(.*\)) returns 0x([0-9A-Fa-f]+) in every one of them$")]
  public void ReturnsInEveryOutput(string text, string hex) {
    var result = ulong.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
    var name = text[..text.IndexOf('(')];
    var method = this._from == "csharp"
      ? PrototypeFixture.Compiled(scenario.Fixture, name)
      : GeneratedCSharp.Compile(this._outputs.First(output => output.Key.CSharp is not null).Value.Text, false).Method(name);
    var call = CallText.Parse(text, method);
    Sweep.Each([.. this._outputs.Keys], target => {
      var (file, generated) = this._outputs[target];
      var outcomes = new List<string>();
      if (target.CSharp is not null)
        outcomes.AddRange(_overflowChecking.Select(checking => GeneratedCSharp.Compile(generated, checking).Outcome(call)));
      else
        outcomes.Add(Toolchain.NodeOutcomes(Path.GetDirectoryName(file)!, Path.GetFileName(file), [call], false, target.JavaScript!.Mode, target.JavaScript.Global).Single());

      Assert.That(outcomes.Where(outcome => !outcome.StartsWith(result + " | ", StringComparison.Ordinal)), Is.Empty, target.ToString());
    });
  }

  private string Output(string profile) {
    if (!this._named.TryGetValue(profile, out var text)) {
      var target = TargetCase.All.First(candidate => candidate.ToString() == profile || candidate.JavaScript?.Level == profile && candidate.JavaScript.Module == (profile == "es5" ? "cjs" : "esm") && !candidate.JavaScript.TypedArrays);
      this._named[profile] = text = this.Translate(target).Text;
    }

    return text;
  }

  /// <summary>The output of a profile calls the library member.</summary>
  [Then("^the " + StepText.Quoted + " output calls " + StepText.Quoted + "$")]
  public void OutputCalls(string profile, string member) => Assert.That(this.Output(profile), Does.Contain(member));

  /// <summary>The output of a profile declares a private helper, the byte swap's, and does not name the library type.</summary>
  [Then("^the " + StepText.Quoted + " output declares a private helper and names no " + StepText.Quoted + "$")]
  public void OutputDeclaresAPrivateHelper(string profile, string type)
    => Assert.That(this.Output(profile), Does.Contain("private static ulong ByteSwapBits(").And.Not.Contain(type));

  /// <summary>The output of a JavaScript profile declares the byte swap's helper function.</summary>
  [Then("^the " + StepText.Quoted + " output declares a helper$")]
  public void OutputDeclaresAHelper(string profile) => Assert.That(this.Output(profile), Does.Contain("function $bswap("));

  /// <summary>Every profile of both backends computes the byte swap.</summary>
  [Then(@"^all of them return 0x([0-9A-Fa-f]+) for (F\(.*\))$")]
  public void AllReturn(string hex, string text) {
    var call = CallText.OfSource(text, File.ReadAllText(scenario.Workspace.Resolve(scenario.Source)));
    this.EveryProfileReturns([call], [uint.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)]);
  }

  /// <summary>The interpreter and every profile of both backends compute the rotation.</summary>
  [Then(@"^(F\(.*\)) returns 0x([0-9A-Fa-f]+) in the interpreter and in every profile of both backends$")]
  public void ReturnsEverywhere(string text, string hex) {
    var call = CallText.OfSource(text, File.ReadAllText(scenario.Workspace.Resolve(scenario.Source)));
    var expected = ulong.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
    Assert.That(PrototypeFixture.InterpretedOutcome(PrototypeFixture.Import("Snippet.cs", File.ReadAllText(scenario.Workspace.Resolve(scenario.Source))), call), Is.EqualTo(expected));
    this.EveryProfileReturns([call], [expected]);
  }

  /// <summary>Both counts of zero, on every width, are the width in every profile.</summary>
  [Then("^LeadingZeroCount and TrailingZeroCount of 0 return 8, 16, 32 and 64 for the four widths in every profile$")]
  public void ZerosOfZeroAreTheWidth() {
    scenario.Snippet(string.Join(" ",
      "public static byte L8(byte x) => byte.LeadingZeroCount(x); public static ushort L16(ushort x) => ushort.LeadingZeroCount(x);",
      "public static int L32(uint x) => System.Numerics.BitOperations.LeadingZeroCount(x); public static int L64(ulong x) => System.Numerics.BitOperations.LeadingZeroCount(x);",
      "public static byte T8(byte x) => byte.TrailingZeroCount(x); public static ushort T16(ushort x) => ushort.TrailingZeroCount(x);",
      "public static int T32(uint x) => System.Numerics.BitOperations.TrailingZeroCount(x); public static int T64(ulong x) => System.Numerics.BitOperations.TrailingZeroCount(x);"));
    PrototypeCall[] calls = [
      new("L8", [(byte)0]), new("L16", [(ushort)0]), new("L32", [0u]), new("L64", [0UL]),
      new("T8", [(byte)0]), new("T16", [(ushort)0]), new("T32", [0u]), new("T64", [0UL])
    ];
    this.EveryProfileReturns(calls, ["8", "16", "32", "64", "8", "16", "32", "64"]);
  }

  /// <summary>The load traps in the interpreter and in every profile of both backends.</summary>
  [Then(@"^F\(new byte\[(\d+)\], (\d+)\) traps with an index out of range in the interpreter and in every profile of both backends$")]
  public void LoadTraps(int length, int index) {
    var call = new PrototypeCall("F", [new byte[length], index]);
    const string trap = "trap: index out of range";
    Assert.That(PrototypeFixture.InterpretedOutcome(PrototypeFixture.Import("Snippet.cs", File.ReadAllText(scenario.Workspace.Resolve(scenario.Source))), call), Is.EqualTo(trap));
    this.EveryProfileReturns([call], [trap]);
  }

  /// <summary>Runs the translate command on the source, to JavaScript.</summary>
  [When("^I run the translate command$")]
  public void TranslateCommand() => scenario.Polypiler.Run("translate", scenario.Source, "--from", "csharp", "--to", "javascript", "--output", "out");

  /// <summary>
  /// The command was refused with a diagnostic naming the member, and wrote nothing. A construct
  /// outside the subset is PLY1002 with exit code 3; PLY1003, which the issue named, is the C#
  /// frontend's code for a constant that overflows.
  /// </summary>
  [Then(@"^the exit code is (\d+) and the output contains " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void Refused(int exitCode, string code, string subject) {
    var result = scenario.Log.Last;
    Assert.That(result.ExitCode, Is.EqualTo(exitCode), result.Output);
    Assert.That(result.Output, Does.Contain(code).And.Contain(subject));
    Assert.That(Directory.Exists(scenario.Workspace.Resolve("out")), Is.False);
  }

}
