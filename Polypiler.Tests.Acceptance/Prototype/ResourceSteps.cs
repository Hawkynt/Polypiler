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
/// The steps of <c>tests/features/ir/ir-resource-scopes.feature</c>. The expected value is never
/// computed here: it is what .NET computes of <c>Scopes.cs</c>, built into this assembly, or what
/// <c>node</c> computes of <c>scopes.js</c>; the log and the message of the scenario are held to those first.
/// </summary>
[Binding, Scope(Feature = "Resource scopes")]
public sealed class ResourceSteps(PrototypeScenario scenario) {

  /// <summary>Each translation of the scenario: the generated file, and the JavaScript profile it was written for or <see langword="null"/> for C#.</summary>
  private readonly List<(string Path, string Text, ProfileCase? Profile)> _generated = [];

  /// <summary>The profile of a level in the module format the level starts with: an ES module, or CommonJS at ECMAScript 5.</summary>
  private static ProfileCase Level(string level) => ProfileCase.All.First(profile => profile.Level == level && !profile.TypedArrays && profile.Module == (level == "es5" ? "cjs" : "esm"));

  private string From => Path.GetExtension(scenario.Source) == ".cs" ? "csharp" : "javascript";

  /// <summary>The fixture that holds the C# method of the scenario, as the source.</summary>
  [Given("^the C# method " + StepText.Quoted + " in " + StepText.Quoted + "$")]
  public void MethodOfFixture(string method, string path) {
    var file = ResourceFixtures.PathOf(Path.GetFileName(path));
    var text = StepText.Unescape(method);
    Assert.That(File.ReadAllText(file), Does.Contain(text[..text.IndexOf(" {", StringComparison.Ordinal)]).And.Contain("await using (r)"));
    scenario.Workspace.Copy(file, path);
    scenario.Source = path;
  }

  /// <summary>Translates the source to C# for a profile, which compiles at its language version.</summary>
  private void ToCSharp(string name) {
    var profile = CSharpProfile.Parse(name);
    var (path, text) = scenario.Translate(scenario.Source, this.From, "csharp", "--profile", profile.Name);
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
    this._generated.Add((path, text, null));
  }

  /// <summary>Translates the source to JavaScript for a level, at which the file is.</summary>
  private void ToJavaScript(string level) {
    var profile = Level(level);
    var (path, text) = scenario.Translate(scenario.Source, this.From, "javascript", profile.Arguments);
    Assert.That(GeneratedJavaScript.Problems(text, profile.Parsed), Is.Empty, text);
    this._generated.Add((path, text, profile));
  }

  /// <summary>Translates the source with the options of the step, which name the target and its profile.</summary>
  [When("^it is translated with " + StepText.Quoted + "$")]
  public void TranslatedWith(string options) {
    var arguments = CommandLine.Split(options).ToList();
    var name = arguments[arguments.IndexOf("--profile") + 1];
    if (arguments[arguments.IndexOf("--to") + 1] == "csharp")
      this.ToCSharp(name);
    else
      this.ToJavaScript(name);
  }

  /// <summary>Translates the source twice, with the options of each.</summary>
  [When("^it is translated with " + StepText.Quoted + " and with " + StepText.Quoted + "$")]
  public void TranslatedWithBoth(string first, string second) {
    this.TranslatedWith(first);
    this.TranslatedWith(second);
  }

  /// <summary>Translates the source to C# for a profile.</summary>
  [When("^it is translated to C# with \"--profile (\\S+)\"$")]
  public void TranslatedToCSharp(string profile) => this.ToCSharp(profile);

  /// <summary>Runs the translate command on the source with the options of the step.</summary>
  [When("^I run the translate command with " + StepText.Quoted + "$")]
  public void TranslateCommandWith(string options)
    => scenario.Polypiler.Run(["translate", scenario.Source, "--from", this.From, "--output", "out", .. CommandLine.Split(options)]);

  /// <summary>The outcome of a method in .NET of the original fixture, held to the scenario's value, in the interpreter and in every translation of the scenario.</summary>
  private void Everywhere(string method, string expected) {
    var call = new PrototypeCall(method, []) { Returns = typeof(string) };
    Assert.That(PrototypeFixture.InvokedOutcome(ResourceFixtures.Compiled(method), call), Is.EqualTo(expected), ".NET");
    Assert.That(PrototypeFixture.InterpretedOutcome(ResourceFixtures.Imported(), call), Is.EqualTo(expected), "the interpreter");
    Assert.That(this._generated, Is.Not.Empty);
    foreach (var (path, text, profile) in this._generated) {
      var outcome = profile is null
        ? GeneratedCSharp.Compile(text, false).Outcome(call)
        : Toolchain.NodeOutcomes(Path.GetDirectoryName(path)!, Path.GetFileName(path), [call], false, profile.Mode, profile.Global)[0];
      Assert.That(outcome, Is.EqualTo(expected), $"{profile?.ToString() ?? "C#"}\n{text}");
    }
  }

  /// <summary>The log as the fixture writes it: the entries separated by commas.</summary>
  private static string Log(string entries) => string.Join(',', entries.Split(',').Select(entry => entry.Trim()));

  /// <summary>A method that leaves without an exception logs the entries.</summary>
  [Then(@"^(\w+)\(\) logs (-?\d+(?:, -?\d+)*)(?: and throws nothing)?$")]
  public void Logs(string method, string entries) => this.Everywhere(method, Log(entries) + "|");

  /// <summary>A method whose scope throws logs the entries, and its caller catches the exception with its message.</summary>
  [Then(@"^(\w+)\(\) logs (-?\d+(?:, -?\d+)*) and the caller catches an X with the message " + StepText.Quoted + "$")]
  public void LogsAndThrows(string method, string entries, string message) => this.Everywhere(method, Log(entries) + "|" + message);

  /// <summary>Whether the method of the generated C# has a using declaration.</summary>
  [Then("^the method (\\w+), which Nest calls, (contains|does not contain) " + StepText.Quoted + "$")]
  public void MethodForm(string method, string has, string text) {
    var (_, generated, _) = this._generated.Single();
    var start = generated.IndexOf($" {method}(", StringComparison.Ordinal);
    Assert.That(start, Is.GreaterThan(0), generated);
    var body = generated[start..generated.IndexOf("\n  }\n", start, StringComparison.Ordinal)];
    Assert.That(body, has == "contains" ? Does.Contain(text) : Does.Not.Contain(text), generated);
    Assert.That(body, Does.Contain("using"), generated);
  }

  /// <summary>The JavaScript fixture translated to C# computes what node computes of the original.</summary>
  [Then(@"^(\w+)\(\) logs (-?\d+(?:, -?\d+)*) and the caller catches an X with the message " + StepText.Quoted + ", as in node$")]
  public void AsInNode(string function, string entries, string message) {
    var expected = Log(entries) + "|" + message;
    var call = new PrototypeCall(function, []) { Returns = typeof(string) };
    var original = Path.Combine(scenario.Workspace.Resolve("."), "original-" + Guid.NewGuid().ToString("N") + ".mjs");
    File.WriteAllText(original, File.ReadAllText(scenario.Workspace.Resolve(scenario.Source)));
    Assert.That(Toolchain.NodeOutcomes(Path.GetDirectoryName(original)!, Path.GetFileName(original), [call])[0], Is.EqualTo(expected), "node");
    var (_, text, _) = this._generated.Single();
    Assert.That(GeneratedCSharp.Compile(text, false).Outcome(call), Is.EqualTo(expected), text);
  }

  /// <summary>The generated file keeps the form of the source.</summary>
  [Then("^the output contains " + StepText.Quoted + " and no " + StepText.Quoted + "$")]
  public void Contains(string present, string absent) {
    var (_, text, _) = this._generated.Single();
    Assert.That(text, Does.Contain(present).And.Not.Contain(absent));
  }

  /// <summary>The translation was refused with its code and the reason, and nothing was written.</summary>
  [Then("^the exit code is (\\d+) and the output contains " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void Refused(int exitCode, string code, string reason) {
    var result = scenario.Log.Last;
    Assert.That(result.ExitCode, Is.EqualTo(exitCode), result.Output);
    Assert.That(result.Output, Does.Contain(code).And.Contain(reason));
    Assert.That(Directory.Exists(scenario.Workspace.Resolve("out")), Is.False);
  }

}
