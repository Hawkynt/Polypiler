using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The steps of <c>tests/features/ir/ir-strings.feature</c>. The expected outcome of a call is never
/// computed here: it is what .NET computes of the original C#, compiled by the C# compiler, or what
/// <c>node</c> computes of the original JavaScript; the values the steps state are checked against both.
/// </summary>
[Binding, Scope(Feature = "Strings of UTF-16 code units")]
public sealed class StringsSteps(PrototypeScenario scenario) {

  private static readonly ProfileCase _oldest = ProfileCase.All.First(profile => profile is { Level: "es5", Module: "cjs", TypedArrays: false });

  private readonly List<(string Target, Func<PrototypeCall, string> Run)> _translations = [];
  private (string Path, string Text) _generated;
  private string _javaScript = "";
  private string _target = "";

  /// <summary>The profile of a level in the module format it starts with.</summary>
  private static ProfileCase Level(string level) => ProfileCase.All.First(profile => profile.Level == level && !profile.TypedArrays && profile.Module == (level == "es5" ? "cjs" : "esm"));

  /// <summary>Runs calls of a generated JavaScript file in node.</summary>
  private static IReadOnlyList<string> Node((string Path, string Text) generated, ProfileCase profile, IReadOnlyList<PrototypeCall> calls)
    => Toolchain.NodeOutcomes(Path.GetDirectoryName(generated.Path)!, Path.GetFileName(generated.Path), calls, true, profile.Mode, profile.Global);

  /// <summary>Runs calls of a JavaScript source of the scenario in node, as it is.</summary>
  private IReadOnlyList<string> Original(string name, IReadOnlyList<PrototypeCall> calls) {
    var module = Path.GetFileNameWithoutExtension(name) + "-original.mjs";
    scenario.Workspace.Copy(scenario.Workspace.Resolve(name), module);
    return Toolchain.NodeOutcomes(scenario.Workspace.Path, module, calls, true);
  }

  /// <summary>Runs a call of the method of <c>Snippet.cs</c> as the C# compiler builds it.</summary>
  private string DotNet(PrototypeCall call) => GeneratedCSharp.Compile(File.ReadAllText(scenario.Workspace.Resolve("Snippet.cs")), false).Outcome(call);

  /// <summary>A generated C# file, compiled for its profile without complaint and then on this runtime.</summary>
  private static GeneratedCSharp Compiled((string Path, string Text) generated, CSharpProfile profile) {
    Assert.That(GeneratedCSharp.ComplaintsFor(generated.Text, profile.Name, profile.LanguageVersionName), Is.Empty, generated.Text);
    return GeneratedCSharp.Compile(generated.Text, false);
  }

  [Given("^the JavaScript function " + StepText.Quoted + "$")]
  public void JavaScriptFunction(string function) {
    // The file names the class of a translation to C#, which must not be named like the function f.
    this._javaScript = "functions.js";
    scenario.Workspace.Write(this._javaScript, StepText.Unescape(function));
    if (scenario.Source.Length == 0)
      scenario.Source = this._javaScript;
  }

  [When("^it is translated with " + StepText.Quoted + "$")]
  public void TranslatedWith(string options) {
    var arguments = CommandLine.Split(options).ToList();
    var to = arguments[arguments.IndexOf("--to") + 1];
    var name = arguments[arguments.IndexOf("--profile") + 1];
    this._target = name;
    var from = Path.GetExtension(scenario.Source) == ".cs" ? "csharp" : "javascript";
    if (to == "javascript") {
      var profile = Level(name);
      this._generated = scenario.Translate(scenario.Source, from, to, profile.Arguments);
      Assert.That(GeneratedJavaScript.Problems(this._generated.Text, profile.Parsed), Is.Empty, this._generated.Text);
      this._translations.Add((name, call => Node(this._generated, profile, [call]).Single()));
    } else {
      var profile = CSharpProfile.Parse(name);
      this._generated = scenario.Translate(scenario.Source, from, to, "--profile", name);
      var build = Compiled(this._generated, profile);
      this._translations.Add((name, build.Outcome));
    }
  }

  [Then(@"^Hash\(""hello""\) returns (\d+) and Hash\(""h\\u00E9llo\\U0001F600""\) returns (\d+)$")]
  public void HashReturns(string first, string second) {
    var method = Path.GetExtension(scenario.Source) == ".cs" ? "Hash" : "hash";
    PrototypeCall[] calls = [new(method, ["hello"]), new(method, ["héllo\U0001F600"])];
    var run = this._translations.Single().Run;
    Assert.That(calls.Select(run), Is.EqualTo(new[] { first, second }), this._target);
    var oracle = method == "Hash"
      ? calls.Select(call => PrototypeFixture.InvokedOutcome(StringFixtures.Compiled("Fnv1aText", call.Method), call))
      : this.Original(scenario.Source, calls);
    Assert.That(oracle, Is.EqualTo(new[] { first, second }));
  }

  [When("^both are translated to the other language in every profile$")]
  public void BothInEveryProfile() {
    foreach (var profile in ProfileCase.All) {
      var generated = scenario.Translate(scenario.Source, "csharp", "javascript", profile.Arguments);
      Assert.That(GeneratedJavaScript.Problems(generated.Text, profile.Parsed), Is.Empty, generated.Text);
      this._translations.Add((profile.ToString(), call => Node(generated, profile, [call]).Single()));
    }

    foreach (var profile in CSharpProfile.All) {
      var build = Compiled(scenario.Translate(this._javaScript, "javascript", "csharp", "--profile", profile.Name), profile);
      this._translations.Add((profile.Name, build.Outcome));
    }
  }

  [Then(@"^F\(""abcb""\) returns ""(\w+)"" and g\(""abcb""\) returns ""(\w+)"" in every profile, as in \.NET and node$")]
  public void ReplaceReturns(string replacedEvery, string replacedFirst) {
    PrototypeCall every = new("F", ["abcb"]), first = new("g", ["abcb"]);
    Assert.That((this.DotNet(every), this.Original(this._javaScript, [first]).Single()), Is.EqualTo((replacedEvery, replacedFirst)));
    foreach (var (target, run) in this._translations)
      Assert.That(run(target.StartsWith("net", StringComparison.Ordinal) ? first : every), Is.EqualTo(target.StartsWith("net", StringComparison.Ordinal) ? replacedFirst : replacedEvery), target);
  }

  /// <summary>A function of one language that returns the length of a trimmed string, translated to the other at its newest and its oldest profile, against the original.</summary>
  [Then(@"^the translation of (C#|JavaScript)'s s\.(Trim|trim)\(\) returns a string of length (\d+) for ""([^""]*)"" and of length (\d+) for ""([^""]*)""$")]
  public void TrimmedLength(string language, string _, int firstLength, string first, int secondLength, string second) {
    string[] texts = [System.Text.RegularExpressions.Regex.Unescape(first), System.Text.RegularExpressions.Regex.Unescape(second)];
    string[] lengths = [firstLength.ToString(System.Globalization.CultureInfo.InvariantCulture), secondLength.ToString(System.Globalization.CultureInfo.InvariantCulture)];
    if (language == "C#") {
      scenario.Snippet("public static int F(string s) => s.Trim().Length;");
      Assert.That(texts.Select(text => this.DotNet(new PrototypeCall("F", [text]))), Is.EqualTo(lengths));
      foreach (var profile in new[] { ProfileCase.Default, _oldest }) {
        var generated = scenario.Translate(scenario.Source, "csharp", "javascript", profile.Arguments);
        Assert.That(Node(generated, profile, [.. texts.Select(text => new PrototypeCall("F", [text]))]), Is.EqualTo(lengths), profile.ToString());
      }
    } else {
      scenario.Script("trim.js", "export function f(s) { return s.trim().length; }");
      PrototypeCall[] calls = [.. texts.Select(text => new PrototypeCall("f", [text]))];
      Assert.That(this.Original("trim.js", calls), Is.EqualTo(lengths));
      foreach (var profile in new[] { CSharpProfile.Default, CSharpProfile.Net20 }) {
        var build = Compiled(scenario.Translate("trim.js", "javascript", "csharp", "--profile", profile.Name), profile);
        Assert.That(calls.Select(build.Outcome), Is.EqualTo(lengths), profile.Name);
      }
    }
  }

  [When("^both are translated to the other language with the default profile and the oldest profile$")]
  public void BothAtTheEnds() {
    foreach (var profile in new[] { ProfileCase.Default, _oldest }) {
      var generated = scenario.Translate(scenario.Source, "csharp", "javascript", profile.Arguments);
      this._translations.Add((profile.ToString(), call => Node(generated, profile, [call]).Single()));
    }

    foreach (var profile in new[] { CSharpProfile.Default, CSharpProfile.Net20 })
      this._translations.Add((profile.Name, Compiled(scenario.Translate(this._javaScript, "javascript", "csharp", "--profile", profile.Name), profile).Outcome));
  }

  [Then(@"^f\(""a\\u\{1F600\}""\) returns (\d+) and G\(""a\\U0001F600""\) returns (\d+) in every translation$")]
  public void CountsReturn(string points, string units) {
    PrototypeCall f = new("f", ["a\U0001F600"]), g = new("G", ["a\U0001F600"]);
    Assert.That((this.Original(this._javaScript, [f]).Single(), this.DotNet(g)), Is.EqualTo((points, units)));
    foreach (var (target, run) in this._translations)
      Assert.That(target.StartsWith("net", StringComparison.Ordinal) ? run(f) : run(g), Is.EqualTo(target.StartsWith("net", StringComparison.Ordinal) ? points : units), target);
  }

  [When("^it is translated to C#$")]
  public void TranslatedToCSharp() {
    this._generated = scenario.Translate(scenario.Source, "javascript", "csharp", "--profile", CSharpProfile.Default.Name);
    this._translations.Add((CSharpProfile.Default.Name, Compiled(this._generated, CSharpProfile.Default).Outcome));
  }

  [Then(@"^f\(""a"", ""B""\) returns (true|false), as in node, and the output contains " + StepText.Quoted + "$")]
  public void ComparesAsInNode(string expected, string text) {
    PrototypeCall call = new("f", ["a", "B"]);
    Assert.That((this.Original(scenario.Source, [call]).Single(), this._translations.Single().Run(call)), Is.EqualTo((expected, expected)));
    Assert.That(this._generated.Text, Does.Contain(text));
  }

  [When("^I run the translate command to JavaScript$")]
  public void TranslateCommandToJavaScript() => scenario.Polypiler.Run("translate", scenario.Source, "--from", "csharp", "--to", "javascript", "--output", "out");

  [When("^I run the translate command to C#$")]
  public void TranslateCommandToCSharp() => scenario.Polypiler.Run("translate", scenario.Source, "--from", "javascript", "--to", "csharp", "--output", "out");

  [Then(@"^the exit code is (\d+) and the output contains " + StepText.Quoted + ", " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void RefusedWithThree(int exitCode, string code, string member, string advice) {
    this.RefusedWith(exitCode, code, member);
    Assert.That(scenario.Log.Last.Output, Does.Contain(advice));
  }

  [Then(@"^the exit code is (\d+) and the output contains " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void RefusedWith(int exitCode, string code, string member) {
    Assert.That(scenario.Log.Last.ExitCode, Is.EqualTo(exitCode), scenario.Log.Last.Output);
    Assert.That(scenario.Log.Last.Output, Does.Contain(code).And.Contain(member));
    Assert.That(Directory.Exists(scenario.Workspace.Resolve("out")), Is.False);
  }

}
