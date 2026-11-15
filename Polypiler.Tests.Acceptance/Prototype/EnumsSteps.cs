using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The steps of <c>tests/features/ir/ir-enums.feature</c>. Every value a step states is checked against
/// the original - .NET running the C# fixture, built into this assembly, or node running the JavaScript
/// fixture - and against every translation the scenario made, each called with the arguments its own
/// signature takes: a member of an enum by its name where a parameter is an enum, its number elsewhere.
/// </summary>
[Binding, Scope(Feature = "Enums as named integer types")]
public sealed partial class EnumsSteps(PrototypeScenario scenario) {

  /// <summary>Every translation: what it is, and how a call of it is run.</summary>
  private readonly List<(string Target, Func<string, string[], string> Run)> _translations = [];
  private string _generated = "";

  /// <summary>A call the steps state: a method with its arguments, and what it returns or the trap it raises.</summary>
  [GeneratedRegex(@"(\w+)\(((?:""(?:[^""\\]|\\.)*""|[^)""])*)\) (returns|traps with) (""(?:[^""\\]|\\.)*""|[^ ,]+)", RegexOptions.CultureInvariant)]
  private static partial Regex StatedCall();

  /// <summary>The arguments of a stated call, separated by commas outside quotes.</summary>
  [GeneratedRegex(@"""(?:[^""\\]|\\.)*""|[^,\s][^,]*", RegexOptions.CultureInvariant)]
  private static partial Regex StatedArgument();

  private static string Unquoted(string text) => text.StartsWith('"') ? Regex.Unescape(text[1..^1]) : text;

  /// <summary>An outcome as the steps write it, in the notation of <see cref="PrototypeFixture.Outcome"/>.</summary>
  private static string Expected(string verb, string value) => verb == "traps with" ? "trap: " + Unquoted(value) : Unquoted(value);

  /// <summary>An argument for a parameter of a .NET method: a string, a truth value, a member of an enum by its name, or a number.</summary>
  private static object Argument(string text, Type parameter) {
    if (text.StartsWith('"'))
      return Unquoted(text);

    if (parameter.IsEnum)
      return Enum.Parse(parameter, text[(text.IndexOf('.') + 1)..]);

    return text is "true" or "false" ? text == "true" : Convert.ChangeType(long.Parse(text, CultureInfo.InvariantCulture), parameter, CultureInfo.InvariantCulture);
  }

  /// <summary>What a .NET method returns for the stated arguments, the trap of Enum.Parse named as the IR names it.</summary>
  private static string Invoked(MethodInfo method, string[] arguments) {
    var values = arguments.Select((argument, index) => Argument(argument, method.GetParameters()[index].ParameterType)).ToArray();
    return EnumFixtures.Normalized(PrototypeFixture.InvokedOutcome(method, new PrototypeCall(method.Name, values)));
  }

  /// <summary>An argument for node: a string, a truth value, or a number - a member of an enum as the number of its value in the original.</summary>
  private object Number(string text) {
    if (text.StartsWith('"'))
      return Unquoted(text);

    if (text is "true" or "false")
      return text == "true";

    if (text.Contains('.') && this.Original() is { } original) {
      var type = original.Assembly.GetType(original.FullName + "+" + text[..text.IndexOf('.')], true)!;
      return Convert.ToInt32(Enum.Parse(type, text[(text.IndexOf('.') + 1)..]), CultureInfo.InvariantCulture);
    }

    return int.Parse(text, CultureInfo.InvariantCulture);
  }

  /// <summary>The class of the C# fixture of the scenario as the C# compiler built it into this assembly, or <see langword="null"/> for a JavaScript one.</summary>
  private Type? Original() {
    if (Path.GetExtension(scenario.Source) != ".cs")
      return null;

    var name = Regex.Match(File.ReadAllText(scenario.Workspace.Resolve(scenario.Source)), @"public static class (\w+)").Groups[1].Value;
    return typeof(EnumsSteps).Assembly.GetType("Polypiler.Fixtures.Prototype." + name, true);
  }

  /// <summary>What the original returns for a call: .NET for a C# fixture, node for a JavaScript one.</summary>
  private string RunOriginal(string method, string[] arguments) {
    if (this.Original() is { } original)
      return Invoked(original.GetMethod(method, BindingFlags.Public | BindingFlags.Static)!, arguments);

    var module = Path.GetFileNameWithoutExtension(scenario.Source) + "-original.mjs";
    if (!File.Exists(scenario.Workspace.Resolve(module)))
      scenario.Workspace.Copy(scenario.Workspace.Resolve(scenario.Source), module);

    return Toolchain.NodeOutcomes(scenario.Workspace.Path, module, [new PrototypeCall(method, [.. arguments.Select(this.Number)])], true).Single();
  }

  private string From => Path.GetExtension(scenario.Source) == ".cs" ? "csharp" : "javascript";

  /// <summary>Translates the source to JavaScript for a profile, which node runs.</summary>
  private void ToJavaScript(ProfileCase profile) {
    var generated = scenario.Translate(scenario.Source, this.From, "javascript", profile.Arguments);
    Assert.That(GeneratedJavaScript.Problems(generated.Text, profile.Parsed), Is.Empty, generated.Text);
    this._generated = generated.Text;
    this._translations.Add((profile.ToString(), (method, arguments) => Toolchain.NodeOutcomes(
      Path.GetDirectoryName(generated.Path)!, Path.GetFileName(generated.Path), [new PrototypeCall(method, [.. arguments.Select(this.Number)])], true, profile.Mode, profile.Global).Single()));
  }

  /// <summary>Translates the source to C# for a profile, which the C# compiler builds for it and this runtime runs.</summary>
  private void ToCSharp(CSharpProfile profile) {
    var generated = scenario.Translate(scenario.Source, this.From, "csharp", "--profile", profile.Name);
    Assert.That(GeneratedCSharp.ComplaintsFor(generated.Text, profile.Name, profile.LanguageVersionName), Is.Empty, generated.Text);
    this._generated = generated.Text;
    var build = GeneratedCSharp.Compile(generated.Text, false);
    this._translations.Add((profile.Name, (method, arguments) => Invoked(build.Method(method), arguments)));
  }

  [Given("^the C# method " + StepText.Quoted + " of " + StepText.Quoted + "$")]
  public void MethodOfFixture(string method, string fixture) {
    new PrototypeSteps(scenario).Fixture(fixture);
    Assert.That(File.ReadAllText(scenario.Workspace.Resolve(scenario.Source)), Does.Contain(StepText.Unescape(method)));
  }

  [Given("^the JavaScript module " + StepText.Quoted + "$")]
  public void JavaScriptModule(string module) => scenario.Script("module.js", StepText.Unescape(module));

  [When("^it is translated with " + StepText.Quoted + "$")]
  public void TranslatedWith(string options) {
    var arguments = CommandLine.Split(options).ToArray();
    if (arguments[Array.IndexOf(arguments, "--to") + 1] == "javascript")
      this.ToJavaScript(ProfileCase.Of(arguments));
    else
      this.ToCSharp(CSharpProfile.Parse(arguments[Array.IndexOf(arguments, "--profile") + 1]));
  }

  [When("^it is translated to JavaScript and to C# with " + StepText.Quoted + "$")]
  public void ToBoth(string options) {
    this.ToJavaScript(ProfileCase.Default);
    this.ToCSharp(CSharpProfile.Parse(CommandLine.Split(options).Last()));
  }

  [When("^it is translated to JavaScript with " + StepText.Quoted + "$")]
  public void ToJavaScriptWith(string options) => this.ToJavaScript(ProfileCase.Of([.. CommandLine.Split(options)]));

  [When("^it is translated to JavaScript$")]
  public void ToJavaScriptByDefault() => this.ToJavaScript(ProfileCase.Default);

  [When("^I run the translate command to C#$")]
  public void TranslateCommandToCSharp() => scenario.Polypiler.Run("translate", scenario.Source, "--from", this.From, "--to", "csharp", "--output", "out");

  /// <summary>Every call the step states returns what it says, in the original and in every translation of it.</summary>
  [Then(@"^((?:\w+\([^)]*\) (?:returns|traps with) .+?))(?: in both)?$")]
  public void CallsReturn(string text) {
    var matches = StatedCall().Matches(text);
    Assert.That(matches, Is.Not.Empty, text);
    Assert.That(this._translations, Is.Not.Empty);
    foreach (Match match in matches) {
      var method = match.Groups[1].Value;
      var arguments = StatedArgument().Matches(match.Groups[2].Value).Select(argument => argument.Value.Trim()).ToArray();
      var expected = Expected(match.Groups[3].Value, match.Groups[4].Value);
      Assert.That(this.RunOriginal(method, arguments), Is.EqualTo(expected), $"the original of {match.Value}");
      foreach (var (target, run) in this._translations)
        Assert.That(run(method, arguments), Is.EqualTo(expected), $"{match.Value} in {target}");
    }
  }

  [Then("^the output contains " + StepText.Quoted + @" and (\w+\(.+)$")]
  public void OutputContains(string text, string calls) {
    Assert.That(this._generated, Does.Contain(StepText.Unescape(text)));
    this.CallsReturn(calls);
  }

  [Then(@"^the exit code is (\d+) and the output contains " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void RefusedWith(int exitCode, string code, string member) {
    Assert.That(scenario.Log.Last.ExitCode, Is.EqualTo(exitCode), scenario.Log.Last.Output);
    Assert.That(scenario.Log.Last.Output, Does.Contain(code).And.Contain(member));
    Assert.That(Directory.Exists(scenario.Workspace.Resolve("out")), Is.False);
  }

}
