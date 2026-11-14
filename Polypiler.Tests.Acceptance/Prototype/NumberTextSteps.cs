using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The steps of <c>tests/features/prototype/number-text.feature</c>. Every value a step states is
/// checked twice: against .NET running the original C#, compiled by the C# compiler, or node running
/// the original JavaScript, and against every translation of it the scenario made.
/// </summary>
[Binding, Scope(Feature = "Numbers to text and back, by the rules of the source language")]
public sealed partial class NumberTextSteps(PrototypeScenario scenario) {

  /// <summary>Every translation: the language of its source, the profile, and how a call of it is run.</summary>
  private readonly List<(bool FromCSharp, string Target, Func<PrototypeCall, string> Run)> _translations = [];
  private (string Path, string Text) _generated;
  private string _javaScript = "";

  /// <summary>A call the steps state: a method with one argument, and what it returns or the trap it raises.</summary>
  [GeneratedRegex(@"(\w+)\(([^)]*)\) (returns|traps with) (""(?:[^""\\]|\\.)*""|[^ ,]+)", RegexOptions.CultureInvariant)]
  private static partial Regex StatedCall();

  /// <summary>The profile of a level in the module format it starts with.</summary>
  private static ProfileCase Level(string level) => ProfileCase.All.First(profile => profile.Level == level && !profile.TypedArrays && profile.Module == (level == "es5" ? "cjs" : "esm"));

  private static string Unquoted(string text) => text.StartsWith('"') ? Regex.Unescape(text[1..^1]) : text;

  /// <summary>An argument as the steps write it: a string in quotes, a truth value or a number.</summary>
  private static object Argument(string text) => text switch {
    _ when text.StartsWith('"') => Unquoted(text),
    "true" or "false" => text == "true",
    _ => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture)
  };

  /// <summary>An outcome as the steps write it, in the notation of <see cref="PrototypeFixture.Outcome"/>.</summary>
  private static string Expected(string verb, string value) => verb == "traps with" ? "trap: " + Unquoted(value) : value switch {
    _ when value.StartsWith('"') => Unquoted(value),
    "NaN" => "NaN",
    "Infinity" => PrototypeFixture.Show(double.PositiveInfinity),
    _ => PrototypeFixture.Show(double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture))
  };

  /// <summary>Runs calls of a JavaScript source of the scenario in node, as it is.</summary>
  private string Original(PrototypeCall call) {
    var module = Path.GetFileNameWithoutExtension(this._javaScript) + "-original.mjs";
    if (!File.Exists(scenario.Workspace.Resolve(module)))
      scenario.Workspace.Copy(scenario.Workspace.Resolve(this._javaScript), module);

    return Toolchain.NodeOutcomes(scenario.Workspace.Path, module, [call], true).Single();
  }

  /// <summary>Runs a call of the method of <c>Snippet.cs</c> as the C# compiler builds it.</summary>
  private string DotNet(PrototypeCall call) => GeneratedCSharp.Compile(File.ReadAllText(scenario.Workspace.Resolve("Snippet.cs")), false).Outcome(call);

  /// <summary>Translates the C# snippet to JavaScript for a profile, which node runs.</summary>
  private void ToJavaScript(ProfileCase profile) {
    var generated = scenario.Translate("Snippet.cs", "csharp", "javascript", profile.Arguments);
    Assert.That(GeneratedJavaScript.Problems(generated.Text, profile.Parsed), Is.Empty, generated.Text);
    this._generated = generated;
    this._translations.Add((true, profile.ToString(),
      call => Toolchain.NodeOutcomes(Path.GetDirectoryName(generated.Path)!, Path.GetFileName(generated.Path), [call], true, profile.Mode, profile.Global).Single()));
  }

  /// <summary>Translates the JavaScript function to C# for a profile, which the C# compiler builds for it and this runtime runs.</summary>
  private void ToCSharp(CSharpProfile profile) {
    var generated = scenario.Translate(this._javaScript, "javascript", "csharp", "--profile", profile.Name);
    Assert.That(GeneratedCSharp.ComplaintsFor(generated.Text, profile.Name, profile.LanguageVersionName), Is.Empty, generated.Text);
    this._generated = generated;
    this._translations.Add((false, profile.Name, GeneratedCSharp.Compile(generated.Text, false).Outcome));
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
    var name = arguments[arguments.IndexOf("--profile") + 1];
    if (arguments[arguments.IndexOf("--to") + 1] == "javascript")
      this.ToJavaScript(Level(name));
    else
      this.ToCSharp(CSharpProfile.Parse(name));
  }

  [When("^both are translated to the other language in every profile$")]
  public void BothInEveryProfile() {
    foreach (var profile in ProfileCase.All)
      this.ToJavaScript(profile);

    foreach (var profile in CSharpProfile.All)
      this.ToCSharp(profile);
  }

  /// <summary>
  /// Every call the step states returns what it says - in the original, run by .NET or node, and in
  /// every translation of it. A method that starts with an upper-case letter is C#'s, any other JavaScript's.
  /// </summary>
  [Then(@"^((?:\w+\([^)]*\) (?:returns|traps with) .+))$")]
  public void CallsReturn(string text) {
    var matches = StatedCall().Matches(text);
    Assert.That(matches, Is.Not.Empty, text);
    foreach (Match match in matches) {
      var call = new PrototypeCall(match.Groups[1].Value, [Argument(match.Groups[2].Value)]);
      var expected = Expected(match.Groups[3].Value, match.Groups[4].Value);
      var fromCSharp = char.IsUpper(call.Method[0]);
      Assert.That(fromCSharp ? this.DotNet(call) : this.Original(call), Is.EqualTo(expected), $"the original of {call}");
      var translations = this._translations.Where(translation => translation.FromCSharp == fromCSharp).ToList();
      Assert.That(translations, Is.Not.Empty, $"no translation of {call.Method}");
      foreach (var (_, target, run) in translations)
        Assert.That(run(call), Is.EqualTo(expected), $"{call} in {target}");
    }
  }

  [Then(@"^the generated header names the caveat " + StepText.Quoted + @" and (\w+\([^)]*\) returns .+)$")]
  public void HeaderNamesTheCaveat(string caveat, string calls) {
    var header = string.Join('\n', this._generated.Text.Split('\n').TakeWhile(line => line.StartsWith("//", StringComparison.Ordinal)));
    Assert.That(header, Does.Contain(caveat));
    foreach (Match match in StatedCall().Matches(calls)) {
      var call = new PrototypeCall(match.Groups[1].Value, [Argument(match.Groups[2].Value)]);
      Assert.That(this._translations.Single().Run(call), Is.EqualTo(Expected(match.Groups[3].Value, match.Groups[4].Value)));
    }
  }

  [When("^I run the translate command to JavaScript$")]
  public void TranslateCommandToJavaScript() => scenario.Polypiler.Run("translate", scenario.Source, "--from", "csharp", "--to", "javascript", "--output", "out");

  [Then(@"^the exit code is (\d+) and the output contains " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void RefusedWith(int exitCode, string code, string member) {
    Assert.That(scenario.Log.Last.ExitCode, Is.EqualTo(exitCode), scenario.Log.Last.Output);
    Assert.That(scenario.Log.Last.Output, Does.Contain(code).And.Contain(member));
    Assert.That(Directory.Exists(scenario.Workspace.Resolve("out")), Is.False);
  }

}
