using System;
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
/// The steps of <c>tests/features/ir/ir-sequence-operations.feature</c> (#1329). The expected outcome of a
/// call is never computed here: it is what .NET computes of the C# fixture, built into this assembly,
/// or what <c>node</c> computes of the original JavaScript; the step checks that oracle against the
/// value the feature states, then the generated code against both.
/// </summary>
[Binding, Scope(Feature = "LINQ and JavaScript array callbacks keep their evaluation order")]
public sealed class LinqSteps(PrototypeScenario scenario) {

  private (string Path, string Text) _generated;
  private ProfileCase? _profile;
  private readonly Dictionary<string, (string Path, string Text)> _translated = [];
  private readonly List<string> _fixtures = [];

  private static bool IsScript(string path) => Path.GetExtension(path) is ".mjs" or ".js";

  /// <summary>The profile of a level in the module format it starts with.</summary>
  private static ProfileCase Level(string level)
    => ProfileCase.All.First(profile => profile.Level == level && !profile.TypedArrays && profile.Module == (level == "es5" ? "cjs" : "esm"));

  /// <summary>The class of a C# fixture.</summary>
  private static string ClassOf(string path) => Path.GetFileNameWithoutExtension(path);

  /// <summary>Splits the arguments of a call at the commas outside brackets.</summary>
  private static List<string> Arguments(string text) {
    var parts = new List<string>();
    var depth = 0;
    var start = 0;
    for (var index = 0; index < text.Length; ++index) {
      depth += text[index] switch { '[' => 1, ']' => -1, _ => 0 };
      if (text[index] == ',' && depth == 0) {
        parts.Add(text[start..index]);
        start = index + 1;
      }
    }

    if (text.Length > 0)
      parts.Add(text[start..]);

    return parts;
  }

  /// <summary>An argument as a value of a type: a number or an array of numbers.</summary>
  private static object Argument(string text, Type type) {
    if (!type.IsArray)
      return Convert.ChangeType(double.Parse(text, CultureInfo.InvariantCulture), type, CultureInfo.InvariantCulture);

    var element = type.GetElementType()!;
    var items = text.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries).Select(item => Argument(item, element)).ToArray();
    var array = Array.CreateInstance(element, items.Length);
    Array.Copy(items, array, items.Length);
    return array;
  }

  /// <summary>A call of a function of a fixture: a C# one with the types of its parameters, a JavaScript one with doubles.</summary>
  private static PrototypeCall Call(string source, string method, string arguments) {
    var texts = Arguments(arguments);
    if (IsScript(source))
      return new(method, [.. texts.Select(text => Argument(text, text.StartsWith('[') ? typeof(double[]) : typeof(double)))]);

    var compiled = LinqFixtures.Compiled(ClassOf(source), method);
    var parameters = compiled.GetParameters();
    return new(method, [.. texts.Select((text, index) => Argument(text, parameters[index].ParameterType))]) { Returns = compiled.ReturnType };
  }

  /// <summary>The result of an outcome, without the arrays the call was given after it.</summary>
  private static string Result(string outcome) => outcome.Split(" | ")[0];

  /// <summary>What the source language computes of a call: .NET of the C# fixture, node of the JavaScript one.</summary>
  private string Original(string source, PrototypeCall call) {
    if (!IsScript(source))
      return Result(PrototypeFixture.InvokedOutcome(LinqFixtures.Compiled(ClassOf(source), call.Method), call));

    var copy = Path.Combine(scenario.Workspace.Resolve("."), "original-" + Guid.NewGuid().ToString("N") + ".mjs");
    File.WriteAllText(copy, SequenceFixtures.SourceOf(Path.GetFileName(source)));
    return Result(Toolchain.NodeOutcomes(Path.GetDirectoryName(copy)!, Path.GetFileName(copy), [call], true).Single());
  }

  /// <summary>What the generated code computes of a call: the compiled C#, or node on the JavaScript of its profile.</summary>
  private static string Generated((string Path, string Text) generated, ProfileCase? profile, PrototypeCall call)
    => Result(profile is null
      ? GeneratedCSharp.Compile(generated.Text, false).Outcome(call)
      : Toolchain.NodeOutcomes(Path.GetDirectoryName(generated.Path)!, Path.GetFileName(generated.Path), [call], true, profile.Mode, profile.Global).Single());

  /// <summary>Translates the fixture with the options of the step, which name the target and the profile; the output compiles or parses at its level.</summary>
  [When("^it is translated with " + StepText.Quoted + "$")]
  public void TranslatedWith(string options) {
    var arguments = CommandLine.Split(options).ToList();
    var to = arguments[arguments.IndexOf("--to") + 1];
    var name = arguments[arguments.IndexOf("--profile") + 1];
    var from = IsScript(scenario.Source) ? "javascript" : "csharp";
    if (to == "javascript") {
      this._profile = Level(name);
      this._generated = scenario.Translate(scenario.Source, from, to, this._profile.Arguments);
      Assert.That(GeneratedJavaScript.Problems(this._generated.Text, this._profile.Parsed), Is.Empty, this._generated.Text);
    } else {
      var profile = CSharpProfile.Parse(name);
      this._profile = null;
      this._generated = scenario.Translate(scenario.Source, from, to, "--profile", name);
      Assert.That(GeneratedCSharp.ComplaintsFor(this._generated.Text, profile.Name, profile.LanguageVersionName), Is.Empty, this._generated.Text);
    }
  }

  /// <summary>A call returns the value the feature states, in the source language and in the generated code.</summary>
  [Then(@"^(\w+)\((.*)\) returns (\S+) in the generated code, as in (?:\.NET|node)$")]
  public void Returns(string method, string arguments, string expected) {
    var call = Call(scenario.Source, method, arguments);
    Assert.That(this.Original(scenario.Source, call), Is.EqualTo(expected), $"{call} in the source language");
    Assert.That(Generated(this._generated, this._profile, call), Is.EqualTo(expected), $"{call}\n{this._generated.Text}");
  }

  /// <summary>A call of the C# fixture returns the value the feature states in the interpreter, as in .NET.</summary>
  [Then(@"^(\w+)\((.*)\) returns (\S+) in the interpreter$")]
  public void Interpreted(string method, string arguments, string expected) {
    var call = Call(scenario.Source, method, arguments);
    Assert.That(this.Original(scenario.Source, call), Is.EqualTo(expected), ".NET");
    Assert.That(Result(PrototypeFixture.InterpretedOutcome(LinqFixtures.Imported(ClassOf(scenario.Source)), call)), Is.EqualTo(expected));
  }

  /// <summary>The generated code defines the stable merge sort and calls it; no sort of the target's library stands in for it.</summary>
  [Then("^the output defines the stable merge sort " + StepText.Quoted + "$")]
  public void DefinesTheSort(string helper) {
    var text = this._generated.Text;
    Assert.That(text, Does.Contain(this._profile is null ? $"static void {helper}(" : $"function {helper}("), text);
    Assert.That(text, Does.Not.Contain("Array.Sort").And.Not.Contain(".sort("), text);
  }

  /// <summary>Copies the two fixtures into the workspace and translates the C# one to JavaScript and the JavaScript one to C#, each with the default profile.</summary>
  [Given("^the fixtures " + StepText.Quoted + " and " + StepText.Quoted + " translated to each other's language$")]
  public void EachTranslated(string first, string second) {
    foreach (var path in new[] { first, second }) {
      scenario.Workspace.Copy(SequenceFixtures.PathOf(Path.GetFileName(path)), path);
      this._fixtures.Add(path);
      this._translated[path] = IsScript(path) ? scenario.Translate(path, "javascript", "csharp") : scenario.Translate(path, "csharp", "javascript");
    }
  }

  /// <summary>A call of the C# fixture computes in the generated JavaScript what .NET computes.</summary>
  [Then(@"^(\w+)\(\) of the C# fixture returns (-?\d+) in the generated JavaScript, as in \.NET$")]
  public void CSharpFixture(string method, string expected) {
    var path = this._fixtures.Single(fixture => !IsScript(fixture));
    var call = Call(path, method, "");
    Assert.That(this.Original(path, call), Is.EqualTo(expected), ".NET");
    var profile = ProfileCase.All.First(candidate => candidate.Level == "es2022" && candidate.Module == "esm" && !candidate.TypedArrays);
    Assert.That(Generated(this._translated[path], profile, call), Is.EqualTo(expected), this._translated[path].Text);
  }

  /// <summary>A call of the JavaScript fixture computes in the generated C# what node computes.</summary>
  [Then(@"^(\w+)\(\) of the JavaScript fixture returns (-?\d+) in the generated C#, as in node$")]
  public void ScriptFixture(string function, string expected) {
    var path = this._fixtures.Single(IsScript);
    var call = Call(path, function, "");
    Assert.That(this.Original(path, call), Is.EqualTo(expected), "node");
    Assert.That(Generated(this._translated[path], null, call), Is.EqualTo(expected), this._translated[path].Text);
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
