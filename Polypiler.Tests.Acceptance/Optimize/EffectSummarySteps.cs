using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Prototype;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Optimize;

/// <summary>
/// The steps of <c>tests/features/optimize/opt-effect-summaries.feature</c> (#1361): the C# source of a
/// scenario in a class with the using System, its translations, what they keep of the calls, and what
/// they compute in every C# profile, compiled by Roslyn, and in node, against the source compiled by
/// Roslyn as the oracle.
/// </summary>
[Binding, Scope(Feature = "Functions confined to a scope are classified and optimised by their effects")]
public sealed class EffectSummarySteps(PrototypeScenario scenario) {

  private const string Usings = "using System; ";

  private readonly List<string> _methods = [];
  private (string Path, string Text) _generated;
  private readonly List<(ProfileCase Profile, string Path, string Text)> _scripts = [];
  private readonly List<(CSharpProfile Profile, string Text)> _csharp = [];

  private string Source => File.ReadAllText(scenario.Workspace.Resolve(scenario.Source));

  private void Write() => scenario.Script("Snippet.cs", Usings + PrototypeFixture.Wrap(string.Join(" ", this._methods)));

  /// <summary>A call of the step's text, its arguments read by the parameter types of the source as Roslyn builds it.</summary>
  private PrototypeCall Call(string text) => CallText.Parse(text, GeneratedCSharp.CompileSource(this.Source).Method(text[..text.IndexOf('(')]));

  /// <summary>What the source does for a call, compiled by Roslyn.</summary>
  private string Expected(PrototypeCall call) => GeneratedCSharp.CompileSource(this.Source).Outcome(call);

  /// <summary>The text of a member of a generated file: from its header to the line that closes it at the same indentation, or the header line alone for an expression body.</summary>
  private static string Member(string text, string header) {
    var lines = text.Split('\n');
    var start = Array.FindIndex(lines, line => line.Contains(header, StringComparison.Ordinal));
    Assert.That(start, Is.Not.Negative, $"no '{header}' in\n{text}");
    if (lines[start].TrimEnd().EndsWith(';'))
      return lines[start];

    var indent = lines[start].Length - lines[start].TrimStart().Length;
    var end = Array.FindIndex(lines, start, line => line.Length > indent && line[indent] == '}' && line[..indent].Trim().Length == 0);
    return string.Join('\n', lines[start..(end < 0 ? lines.Length : end + 1)]);
  }

  [Given("^" + StepText.Quoted + "$")]
  public void Methods(string methods) {
    this._methods.Add(StepText.Unescape(methods));
    this.Write();
  }

  [When("^it is translated(?: with the default optimisation level)?$")]
  public void Translated() => this._generated = scenario.Translate(scenario.Source, "csharp", "csharp", "--profile", "net10.0");

  /// <summary>Translates the source for every C# profile and every JavaScript level, module format and array form.</summary>
  [When("^it is translated to every profile(?: of both backends)?$")]
  public void TranslatedEverywhere() {
    foreach (var profile in CSharpProfile.All) {
      var (_, text) = scenario.Translate(scenario.Source, "csharp", "csharp", "--profile", profile.Name);
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
      this._csharp.Add((profile, text));
    }

    foreach (var profile in ProfileCase.All) {
      var (path, text) = scenario.Translate(scenario.Source, "csharp", "javascript", profile.Arguments);
      Assert.That(GeneratedJavaScript.Problems(text, profile.Parsed), Is.Empty, text);
      this._scripts.Add((profile, path, text));
    }
  }

  /// <summary>Every C# profile, compiled by Roslyn, and every JavaScript variant in node computes what .NET computes of the source.</summary>
  private void Everywhere(PrototypeCall call, string value) {
    var expected = this.Expected(call);
    Assert.That(expected.Split(" | ")[0], Is.EqualTo(value));
    Assert.That(this._csharp, Has.Count.EqualTo(CSharpProfile.All.Count));
    foreach (var (profile, text) in this._csharp)
      Assert.That(GeneratedCSharp.Compile(text, false).Outcome(call), Is.EqualTo(expected), $"{profile.Name}\n{text}");

    Assert.That(this._scripts, Has.Count.EqualTo(ProfileCase.All.Count));
    foreach (var (profile, path, text) in this._scripts)
      Assert.That(Toolchain.NodeOutcomes(Path.GetDirectoryName(path)!, Path.GetFileName(path), [call], false, profile.Mode, profile.Global).Single(), Is.EqualTo(expected), $"{profile}\n{text}");
  }

  [Then(@"^(\w+\(.*\)) returns (-?\d+) everywhere$")]
  public void ReturnsEverywhere(string text, string value) => this.Everywhere(this.Call(text), value);

  /// <summary>Where the delegate stays - with the passes off - .NET Framework 2.0 writes an anonymous method; .NET 10 with the passes on has none left.</summary>
  [Then("^for net2.0 the output writes an anonymous method, and for net10.0 no delegate remains$")]
  public void AnonymousMethodAndNoDelegate() {
    var (_, kept) = scenario.Translate(scenario.Source, "csharp", "csharp", "--profile", "net2.0", "--opt", "none");
    Assert.That(kept, Does.Contain("delegate (int x)").And.Not.Contain("=>"), kept);
    var net10 = this._csharp.Single(pair => pair.Profile.Name == "net10.0").Text;
    Assert.That(net10, Does.Not.Contain("delegate").And.Not.Contain("Func").And.Not.Contain("=>"), net10);
  }

  [Then(@"^(\w+) returns the constant (-?\d+) with no call$")]
  public void ReturnsTheConstant(string method, string value) {
    var member = Member(this._generated.Text, $"public static int {method}(");
    Assert.That(member, Does.Match($@"=> {value};|return {value};"), member);
    Assert.That(this.Expected(new PrototypeCall(method, [])).Split(" | ")[0], Is.EqualTo(value));
  }

  /// <summary>Adds a public method <c>R</c> that makes the call, translates, and finds the call in <c>R</c>.</summary>
  [Then("^" + StepText.Quoted + @" \(over the step budget\) stays a call$")]
  public void StaysACall(string call) {
    this._methods.Add($"public static int R() => {StepText.Unescape(call)};");
    this.Write();
    var output = "out-" + Guid.NewGuid().ToString("N");
    var result = scenario.Polypiler.Run(["translate", scenario.Source, "--from", "csharp", "--to", "csharp", "--output", output, "--profile", "net10.0", "--explain-opt"]);
    Assert.That(result.ExitCode, Is.Zero, result.Output);
    Assert.That(result.Output, Does.Contain("O0002 compile-time evaluation: 'R' keeps its call of 'Fib'"), result.Output);
    var text = File.ReadAllText(Directory.GetFiles(scenario.Workspace.Resolve(output)).Single(path => Path.GetFileName(path) != "polypiler.manifest.json"));
    Assert.That(Member(text, "public static int R("), Does.Contain(StepText.Unescape(call)), text);
    Assert.That(Member(text, "public static int G("), Does.Not.Contain("Fib("), text);
  }

  [Then("^each loop pass computes lim once$")]
  public void ComputesOnce() {
    var text = this._generated.Text;
    var loop = Member(Member(text, "public static int H("), "while (");
    Assert.That(Regex.Matches(loop, @"\bH_lim\(k\)"), Has.Count.EqualTo(1), loop);
    Assert.That(loop, Does.Match(@"\(\w+ = H_lim\(k\)\)"), loop);
  }

  [Then(@"^(\w+\(.*\)) returns (-?\d+)$")]
  public void Returns(string text, string value) {
    var call = this.Call(text);
    Assert.That(this.Expected(call).Split(" | ")[0], Is.EqualTo(value));
    Assert.That(GeneratedCSharp.Compile(this._generated.Text, false).Outcome(call), Is.EqualTo(this.Expected(call)), this._generated.Text);
  }

  /// <summary>The calls of <c>inc</c> in a function: every <c>inc()</c> that does not declare it.</summary>
  private static int Calls(string function) => Regex.Matches(function, @"(?<!function |int )\binc\(\)").Count;

  [Then(@"^(W\(\)) returns (\d+) and both calls remain$")]
  public void ReturnsAndBothCallsRemain(string text, string value) {
    this.Everywhere(this.Call(text), value);
    foreach (var (profile, generated) in this._csharp)
      Assert.That(Calls(Member(generated, "public static int W(")), Is.EqualTo(2), $"{profile.Name}\n{generated}");

    foreach (var (profile, _, generated) in this._scripts)
      Assert.That(Calls(Member(generated, "function W(")), Is.EqualTo(2), $"{profile}\n{generated}");
  }

}
