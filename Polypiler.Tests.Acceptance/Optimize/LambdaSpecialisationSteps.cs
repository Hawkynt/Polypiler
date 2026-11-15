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
/// The steps of <c>tests/features/optimize/opt-lambda-specialisation.feature</c> (#1360): the C# source
/// of a scenario in a class with the usings it needs, its translations, the shape of what they hold, and
/// what they compute in every C# profile, compiled by Roslyn, and in node, against the source compiled
/// by Roslyn as the oracle.
/// </summary>
[Binding, Scope(Feature = "Lambdas that stay local are bound and optimised away")]
public sealed class LambdaSpecialisationSteps(PrototypeScenario scenario) {

  private const string Usings = "using System; using System.Linq; ";

  private readonly List<string> _methods = [];
  private (string Path, string Text) _generated;
  private readonly List<(ProfileCase Profile, string Path, string Text)> _scripts = [];
  private readonly List<(CSharpProfile Profile, string Text)> _csharp = [];

  private static ProfileCase Level(string level) => ProfileCase.All.First(profile => profile.Level == level && !profile.TypedArrays && profile.Module == (level == "es5" ? "cjs" : "esm"));

  private string Source => File.ReadAllText(scenario.Workspace.Resolve(scenario.Source));

  private void Write() => scenario.Script("Snippet.cs", Usings + PrototypeFixture.Wrap(string.Join(" ", this._methods)));

  /// <summary>A call of the step's text, its arguments read by the parameter types of the source as Roslyn builds it.</summary>
  private PrototypeCall Call(string text) {
    text = text.Replace("int.MaxValue", "2147483647", StringComparison.Ordinal);
    return CallText.Parse(text, GeneratedCSharp.CompileSource(this.Source).Method(text[..text.IndexOf('(')]));
  }

  /// <summary>What the source does for a call, compiled by Roslyn.</summary>
  private string Expected(PrototypeCall call) => GeneratedCSharp.CompileSource(this.Source).Outcome(call);

  /// <summary>The text of a function or method of a generated file: from its header to the line that closes it at the same indentation.</summary>
  private static string Member(string text, string header) {
    var lines = text.Split('\n');
    var start = Array.FindIndex(lines, line => line.Contains(header, StringComparison.Ordinal));
    Assert.That(start, Is.Not.Negative, $"no '{header}' in\n{text}");
    var indent = lines[start].Length - lines[start].TrimStart().Length;
    var end = Array.FindIndex(lines, start, line => line.Length > indent && line[indent] == '}' && line[..indent].Trim().Length == 0);
    return string.Join('\n', lines[start..(end < 0 ? lines.Length : end + 1)]);
  }

  [Given("^the C# source " + StepText.Quoted + "$")]
  public void CSharpSource(string methods) {
    this._methods.Add(StepText.Unescape(methods));
    this.Write();
  }

  [Given("^" + StepText.Quoted + "$")]
  public void Methods(string methods) => this.CSharpSource(methods);

  /// <summary>Translates the source with the options of the step; C# to C# where they name neither.</summary>
  [When("^it is translated with " + StepText.Quoted + "$")]
  public void TranslatedWith(string options) {
    var arguments = CommandLine.Split(options).ToList();
    string Take(string option) {
      var index = arguments.IndexOf(option);
      if (index < 0)
        return "csharp";

      var value = arguments[index + 1];
      arguments.RemoveRange(index, 2);
      return value;
    }

    var from = Take("--from");
    var to = Take("--to");
    this._generated = scenario.Translate(scenario.Source, from, to, [.. arguments]);
  }

  [When("^it is translated$")]
  public void Translated() => this.TranslatedWith("--profile net10.0");

  /// <summary>Adds a public method <c>R</c> that makes the call of the step, and translates.</summary>
  [When("^" + StepText.Quoted + " is translated$")]
  public void CallTranslated(string call) {
    this._methods.Add($"public static int R() => {StepText.Unescape(call)};");
    this.Write();
    this.Translated();
  }

  [When("^the method is translated with \"--to javascript\" at \"(es5)\" and at \"(es2022)\"$")]
  public void TranslatedToJavaScript(string first, string second) {
    foreach (var profile in new[] { first, second }.Select(Level)) {
      var (path, text) = scenario.Translate(scenario.Source, "csharp", "javascript", profile.Arguments);
      this._scripts.Add((profile, path, text));
    }
  }

  /// <summary>Translates the source for every C# profile and every JavaScript level, module format and array form.</summary>
  [When("^it is translated to every profile of both backends$")]
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

  [Then("^the output contains no delegate type, no lambda and no call inside the loop$")]
  public void NoDelegate() {
    var text = this._generated.Text;
    Assert.That(text, Does.Not.Contain("Func").And.Not.Contain("Action").And.Not.Contain("=>").And.Not.Contain("delegate"), text);
    var loop = Member(Member(text, "public static int F("), "while (");
    Assert.That(Regex.Matches(loop, @"(\w+) ?\(").Select(match => match.Groups[1].Value).Distinct(), Is.EquivalentTo(new[] { "while", "if", "checked" }), loop);
    Assert.That(Regex.Matches(text, @"static \w+ (\w+)\(").Select(match => match.Groups[1].Value), Is.EqualTo(new[] { "F" }), "the fused function and its copies are gone");
  }

  [Then("^the loop body compares the element with 3 and adds it with overflow checking$")]
  public void ComparesAndAdds() {
    var loop = Member(Member(this._generated.Text, "public static int F("), "while (");
    Assert.That(loop, Does.Match(@"if \(\w+ > 3\)").And.Match(@"checked\(\w+ \+ \w+\)"), loop);
  }

  [Then(@"^(F\(.*\)) returns (-?\d+), and (F\(.*\)) throws (\w+Exception)$")]
  public void ReturnsAndThrows(string returning, string value, string throwing, string exception) {
    var build = GeneratedCSharp.Compile(this._generated.Text, false);
    var call = this.Call(returning);
    Assert.That(build.Outcome(call), Is.EqualTo(this.Expected(call)));
    Assert.That(build.Outcome(call).Split(" | ")[0], Is.EqualTo(value));
    var trapping = this.Call(throwing);
    Assert.That(build.Outcome(trapping), Is.EqualTo(this.Expected(trapping)).And.StartsWith("trap:"));
    var thrown = Assert.Throws<System.Reflection.TargetInvocationException>(() => build.Method("F").Invoke(null, trapping.FreshArguments()))!;
    Assert.That(thrown.InnerException?.GetType().Name, Is.EqualTo(exception));
  }

  [Then("^the output passes the lambdas as delegates as before$")]
  public void PassesDelegates() {
    var text = this._generated.Text;
    Assert.That(text, Does.Contain("public static int F(int[] a) => F_Sum(a, static x => x, static x => x > 3);"), text);
    Assert.That(text, Does.Contain("global::System.Func<int, int> select, global::System.Func<int, bool> where"), text);
  }

  [Then("^the output contains no function expression inside F$")]
  public void NoFunctionExpression() {
    foreach (var (profile, _, text) in this._scripts) {
      var function = Member(text, "function F(");
      Assert.That(function, Does.Not.Contain("function (").And.Not.Contain("=>"), $"{profile}\n{text}");
      Assert.That(Regex.Matches(text, @"^(?:export )?function (F\w*)\(", RegexOptions.Multiline).Select(match => match.Groups[1].Value), Is.EqualTo(new[] { "F" }), $"{profile}\n{text}");
    }
  }

  [Then(@"^(F\(.*\)) returns (-?\d+) in node$")]
  public void ReturnsInNode(string text, string value) {
    var call = this.Call(text);
    Assert.That(this.Expected(call).Split(" | ")[0], Is.EqualTo(value));
    foreach (var (profile, path, script) in this._scripts)
      Assert.That(Toolchain.NodeOutcomes(Path.GetDirectoryName(path)!, Path.GetFileName(path), [call], false, profile.Mode, profile.Global).Single(), Is.EqualTo(this.Expected(call)), $"{profile}\n{script}");
  }

  [Then("^G contains a loop multiplying by k and no call of Apply$")]
  public void LoopInG() {
    var text = this._generated.Text;
    var g = Member(text, "public static int G(");
    Assert.That(Member(g, "while ("), Does.Match(@"\w+ \* k\b"), g);
    Assert.That(text, Does.Not.Contain("Apply").And.Not.Contain("Func"), text);
  }

  [Then(@"^(\w+\(.*\)) returns (-?\d+)$")]
  public void Returns(string text, string value) {
    var call = this.Call(text);
    Assert.That(this.Expected(call).Split(" | ")[0], Is.EqualTo(value));
    Assert.That(GeneratedCSharp.Compile(this._generated.Text, false).Outcome(call), Is.EqualTo(this.Expected(call)), this._generated.Text);
  }

  [Then("^H is not specialised and the lambda is passed as a delegate$")]
  public void NotSpecialised() {
    var text = this._generated.Text;
    Assert.That(Member(text, "public static int R("), Does.Contain("H(").And.Contain("x => x + 1"), text);
    Assert.That(text, Does.Contain("private static int H(global::System.Func<int, int> f)").And.Not.Contain("_Bound").And.Not.Contain("_Lambda"), text);
  }

  /// <summary>Every C# profile, compiled by Roslyn, and every JavaScript variant in node computes what .NET computes of the source.</summary>
  private void Everywhere(PrototypeCall call, string? value) {
    var expected = this.Expected(call);
    if (value is not null)
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

  [Then(@"^(\w+\(.*\)) returns what \.NET returns everywhere$")]
  public void ReturnsWhatDotNetReturnsEverywhere(string text) => this.Everywhere(this.Call(text), null);

  [Then("^mix is a static local function from C# 8, a local function in C# 7.3, a private static method below that and a function of the module in JavaScript$")]
  public void StaticForms() {
    foreach (var (profile, text) in this._csharp) {
      var form = profile.LanguageVersion.Major >= 8 ? @"^\s{4,}static int H_mix\(int x\)" : profile.LanguageVersion.Major == 7 ? @"^\s{4,}int H_mix\(int x\)" : @"^  private static int H_mix\(int x\)";
      Assert.That(Regex.IsMatch(text, form, RegexOptions.Multiline), Is.True, $"{profile.Name}\n{text}");
      Assert.That(text, Does.Not.Contain("Func").And.Not.Contain("delegate").And.Not.Match(@"\w =>"), $"{profile.Name}\n{text}");
    }

    // A function of the module, wherever the module format puts those: not inside H.
    foreach (var (profile, _, text) in this._scripts) {
      Assert.That(Regex.IsMatch(text, @"^\s*(export )?function H_mix\(x\)", RegexOptions.Multiline), Is.True, $"{profile}\n{text}");
      Assert.That(Member(text, "function H("), Does.Not.Contain("function H_mix"), $"{profile}\n{text}");
    }
  }

  [Then("^inc stays a closure: a local function from C# 7.3, a lambda in a local below that and a nested function declaration in JavaScript$")]
  public void CapturingForms() {
    foreach (var (profile, text) in this._csharp) {
      var w = Member(text, "public static int W(");
      var form = profile.LanguageVersion.Major >= 7 ? @"^\s+void inc\(\)" : profile.LanguageVersion.Major >= 3 ? @"inc = \(\) =>" : @"inc = delegate";
      Assert.That(Regex.IsMatch(w, form, RegexOptions.Multiline), Is.True, $"{profile.Name}\n{text}");
      Assert.That(w, Does.Not.Contain("static void inc"), $"{profile.Name}\n{text}");
    }

    foreach (var (profile, _, text) in this._scripts)
      Assert.That(Regex.IsMatch(Member(text, "function W("), @"^\s+function inc\(\) \{", RegexOptions.Multiline), Is.True, $"{profile}\n{text}");
  }

}
