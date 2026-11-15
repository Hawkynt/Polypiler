using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The steps of <c>tests/features/prototype/sequences.feature</c> (#1327). The expected outcome of a
/// call is never computed here: it is what .NET computes of the original C# fixture, built into this
/// assembly, or what <c>node</c> computes of the original JavaScript.
/// </summary>
[Binding, Scope(Feature = "Iterators and generators")]
public sealed class SequencesSteps(PrototypeScenario scenario) {

  private (string Path, string Text) _generated;
  private JavaScriptProfile? _javascript;
  private ProfileCase? _profile;
  private readonly List<string> _fixtures = [];
  private readonly Dictionary<string, (string Path, string Text)> _translated = [];

  /// <summary>The profile of a level in the module format the step names, or the one the level starts with.</summary>
  private static ProfileCase Level(string level, string? module)
    => ProfileCase.All.First(profile => profile.Level == level && !profile.TypedArrays && profile.Module == (module ?? (level == "es5" ? "cjs" : "esm")));

  private static bool IsScript(string path) => Path.GetExtension(path) == ".js";

  /// <summary>What the source language computes of a call: .NET of the C# fixture, node of the JavaScript one.</summary>
  private string Original(string source, PrototypeCall call) {
    if (!IsScript(source))
      return PrototypeFixture.InvokedOutcome(SequenceFixtures.Compiled(call.Method), call);

    var copy = Path.Combine(scenario.Workspace.Resolve("."), "original-" + Guid.NewGuid().ToString("N") + ".mjs");
    File.WriteAllText(copy, SequenceFixtures.SourceOf("iterators.js"));
    return Toolchain.NodeOutcomes(Path.GetDirectoryName(copy)!, Path.GetFileName(copy), [call], true).Single();
  }

  /// <summary>What the generated code computes of a call: the compiled C#, or node on the JavaScript of its profile.</summary>
  private static string Generated((string Path, string Text) generated, ProfileCase? profile, PrototypeCall call)
    => profile is null
      ? GeneratedCSharp.Compile(generated.Text, false).Outcome(call)
      : Toolchain.NodeOutcomes(Path.GetDirectoryName(generated.Path)!, Path.GetFileName(generated.Path), [call], true, profile.Mode, profile.Global).Single();

  /// <summary>A call of a method of the fixture; a JavaScript function takes its numbers as doubles.</summary>
  private static PrototypeCall Call(string source, string method, IEnumerable<int> arguments)
    => IsScript(source) ? new(char.ToLowerInvariant(method[0]) + method[1..], [.. arguments.Select(argument => (object)(double)argument)])
      : new(method, [.. arguments.Cast<object>()]) { Returns = SequenceFixtures.Compiled(method).ReturnType };

  /// <summary>Translates the fixture with the options of the step, which name the target and the profile.</summary>
  [When("^it is translated with " + StepText.Quoted + "$")]
  public void TranslatedWith(string options) {
    var arguments = CommandLine.Split(options).ToList();
    var to = arguments[arguments.IndexOf("--to") + 1];
    var name = arguments[arguments.IndexOf("--profile") + 1];
    var from = IsScript(scenario.Source) ? "javascript" : "csharp";
    if (to == "javascript") {
      this._profile = Level(name, arguments.Contains("--module") ? arguments[arguments.IndexOf("--module") + 1] : null);
      this._javascript = this._profile.Parsed;
      this._generated = scenario.Translate(scenario.Source, from, to, this._profile.Arguments);
      Assert.That(GeneratedJavaScript.Problems(this._generated.Text, this._javascript), Is.Empty, this._generated.Text);
    } else {
      var profile = CSharpProfile.Parse(name);
      this._generated = scenario.Translate(scenario.Source, from, to, "--profile", name);
      Assert.That(GeneratedCSharp.ComplaintsFor(this._generated.Text, profile.Name, profile.LanguageVersionName), Is.Empty, this._generated.Text);
    }
  }

  /// <summary>The trace of an early break and of a full enumeration, in the generated code and in the source language.</summary>
  [Then(@"^EarlyBreak returns (\d+) and Full returns (\d+) in the generated code, as in the source language$")]
  public void EarlyBreakAndFull(string earlyBreak, string full) {
    foreach (var (method, expected) in new[] { ("EarlyBreak", earlyBreak), ("Full", full) }) {
      var call = Call(scenario.Source, method, []);
      Assert.That(this.Original(scenario.Source, call), Is.EqualTo(expected), "the source language");
      Assert.That(Generated(this._generated, this._profile, call), Is.EqualTo(expected), this._generated.Text);
    }
  }

  /// <summary>Calls of the generated JavaScript, each as the step lists it, which .NET computes alike.</summary>
  [Then(@"^((?:\w+\(-?\d*\) returns -?\d+(?:, | and )?)+)$")]
  public void Returns(string list) {
    foreach (Match match in Regex.Matches(list, @"(\w+)\((-?\d*)\) returns (-?\d+)")) {
      var arguments = match.Groups[2].Value.Length == 0 ? [] : new[] { int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) };
      var call = Call(scenario.Source, match.Groups[1].Value, arguments);
      Assert.That(this.Original(scenario.Source, call), Is.EqualTo(match.Groups[3].Value), $"{call} in the source language");
      Assert.That(Generated(this._generated, this._profile, call), Is.EqualTo(match.Groups[3].Value), $"{call}\n{this._generated.Text}");
    }
  }

  /// <summary>Copies the two fixtures into the workspace.</summary>
  [Given("^the fixtures " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void Fixtures(string first, string second) {
    foreach (var path in new[] { first, second }) {
      scenario.Workspace.Copy(SequenceFixtures.PathOf(Path.GetFileName(path)), path);
      this._fixtures.Add(path);
    }
  }

  /// <summary>Translates the C# fixture to JavaScript and the JavaScript one to C#, each without a profile.</summary>
  [When("^each is translated to the other language with the default profile$")]
  public void EachTranslated() {
    foreach (var path in this._fixtures)
      this._translated[path] = IsScript(path) ? scenario.Translate(path, "javascript", "csharp") : scenario.Translate(path, "csharp", "javascript");
  }

  /// <summary>The C# fixture's sequence restarts: in .NET and in the JavaScript generated of it.</summary>
  [Then(@"^Twice\(\) of the C# fixture returns (\d+) in \.NET and in the generated JavaScript$")]
  public void CSharpTwice(string expected) {
    var path = this._fixtures.Single(fixture => !IsScript(fixture));
    var call = Call(path, "Twice", []);
    Assert.That(this.Original(path, call), Is.EqualTo(expected), ".NET");
    var profile = ProfileCase.All.First(candidate => candidate.Parsed.Level == JavaScriptProfile.Default.Level && candidate.Module == "esm" && !candidate.TypedArrays);
    Assert.That(Generated(this._translated[path], profile, call), Is.EqualTo(expected), this._translated[path].Text);
  }

  /// <summary>The JavaScript fixture's generator object runs once: in node and in the C# generated of it.</summary>
  [Then(@"^twice\(\) of the JavaScript fixture returns (\d+) in node and in the generated C#$")]
  public void JavaScriptTwice(string expected) {
    var path = this._fixtures.Single(IsScript);
    var call = Call(path, "twice", []);
    Assert.That(this.Original(path, call), Is.EqualTo(expected), "node");
    Assert.That(Generated(this._translated[path], null, call), Is.EqualTo(expected), this._translated[path].Text);
  }

  /// <summary>The generated JavaScript has none of the syntax of generators and iterables.</summary>
  [Then(@"^the output contains no ""function\*"", no ""yield"", no ""for \("" with ""of"" and no ""Symbol""$")]
  public void NoGeneratorSyntax() {
    var text = this._generated.Text;
    Assert.That(text, Does.Not.Contain("function*").And.Not.Contain("yield").And.Not.Contain("Symbol"));
    Assert.That(Regex.IsMatch(text, @"for \([^)]*\bof\b"), Is.False, text);
  }

  /// <summary>The ECMAScript 5 output parses at its level and computes what the original computes in node.</summary>
  [Then(@"^it parses at ECMAScript 5 and earlyBreak\(\) returns (\d+) and fibSum\((\d+)\) returns (\d+) in node$")]
  public void RunsAtEs5(string earlyBreak, int count, string sum) {
    Assert.That(this._javascript!.Level, Is.EqualTo(JavaScriptLevel.Es5));
    Assert.That(GeneratedJavaScript.Problems(this._generated.Text, this._javascript), Is.Empty);
    foreach (var (call, expected) in new[] { (Call(scenario.Source, "earlyBreak", []), earlyBreak), (Call(scenario.Source, "fibSum", [count]), sum) }) {
      Assert.That(this.Original(scenario.Source, call), Is.EqualTo(expected), "node on the original");
      Assert.That(Generated(this._generated, this._profile, call), Is.EqualTo(expected), this._generated.Text);
    }
  }

  /// <summary>Writes a JavaScript source into the workspace as the source.</summary>
  [Given("^the JavaScript function " + StepText.Quoted + "$")]
  public void JavaScriptFunction(string text) => scenario.Script("snippet.js", StepText.Unescape(text));

  /// <summary>Runs the translate command on the JavaScript source, to C#.</summary>
  [When("^I run the translate command to C#$")]
  public void ToCSharp() => scenario.Polypiler.Run("translate", scenario.Source, "--from", "javascript", "--to", "csharp", "--output", "out");

  /// <summary>Runs the translate command on the C# source, to JavaScript.</summary>
  [When("^I run the translate command to JavaScript$")]
  public void ToJavaScript() => scenario.Polypiler.Run("translate", scenario.Source, "--from", "csharp", "--to", "javascript", "--output", "out");

  /// <summary>The command was refused with the exit code and a diagnostic naming the construct, and wrote nothing.</summary>
  [Then(@"^the exit code is (\d+) and the output contains " + StepText.Quoted + " and " + StepText.Quoted + " and nothing is written$")]
  public void Refused(int exitCode, string code, string subject) {
    var result = scenario.Log.Last;
    Assert.That(result.ExitCode, Is.EqualTo(exitCode), result.Output);
    Assert.That(result.Output, Does.Contain(code).And.Contain(subject));
    Assert.That(Directory.Exists(scenario.Workspace.Resolve("out")), Is.False);
  }

}
