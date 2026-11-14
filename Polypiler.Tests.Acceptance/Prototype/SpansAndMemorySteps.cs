using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The steps of <c>tests/features/prototype/spans-and-memory.feature</c> (#1325). An expected value is
/// never computed here: it is what .NET computes of the C# fixture built into this assembly, or what
/// <c>node</c> computes of the original JavaScript, and a trap of a view is compared by its kind.
/// </summary>
[Binding, Scope(Feature = "Views of arrays in both languages and every profile")]
public sealed class SpansAndMemorySteps(PrototypeScenario scenario) {

  private (string Path, string Text) _generated;
  private ProfileCase? _profile;
  private CSharpProfile? _csharp;

  /// <summary>The profile of a level in the module format the level starts with: an ES module, or CommonJS at ECMAScript 5.</summary>
  private static ProfileCase Level(string level) => ProfileCase.All.First(profile => profile.Level == level && !profile.TypedArrays && profile.Module == (level == "es5" ? "cjs" : "esm"));

  private static string Language(string source) => Path.GetExtension(source) == ".cs" ? "csharp" : "javascript";

  /// <summary>What node computes of a generated or original module for calls.</summary>
  private static IReadOnlyList<string> Node(string path, ProfileCase profile, IReadOnlyList<PrototypeCall> calls)
    => Toolchain.NodeOutcomes(Path.GetDirectoryName(path)!, Path.GetFileName(path), calls, true, profile.Mode, profile.Global);

  /// <summary>What the generated file computes for calls: compiled C#, or node for JavaScript.</summary>
  private IReadOnlyList<string> Generated(IReadOnlyList<PrototypeCall> calls) {
    if (this._profile is { } profile)
      return Node(this._generated.Path, profile, calls);

    var build = GeneratedCSharp.Compile(this._generated.Text, true);
    return [.. calls.Select(call => SpanFixtures.Outcome(build.Method(call.Method), call))];
  }

  /// <summary>The result of an outcome without the arrays the call was given.</summary>
  private static string Result(string outcome) => outcome.Split(" | ")[0];

  [When("^it is translated with " + StepText.Quoted + "$")]
  public void TranslatedWith(string options) {
    var arguments = CommandLine.Split(options).ToList();
    var to = arguments[arguments.IndexOf("--to") + 1];
    var name = arguments[arguments.IndexOf("--profile") + 1];
    if (to == "javascript") {
      this._profile = Level(name);
      this._generated = scenario.Translate(scenario.Source, Language(scenario.Source), to, this._profile.Arguments);
      Assert.That(GeneratedJavaScript.Problems(this._generated.Text, this._profile.Parsed), Is.Empty, this._generated.Text);
    } else {
      this._csharp = CSharpProfile.Parse(name);
      this._generated = scenario.Translate(scenario.Source, Language(scenario.Source), to, "--profile", name);
      Assert.That(GeneratedCSharp.ComplaintsFor(this._generated.Text, name, this._csharp.LanguageVersionName), Is.Empty, this._generated.Text);
    }
  }

  [Then(@"^Sum\(d, 4, 4\) returns (\d+), Word\(d\) returns (\d+), CastXor\(d, 8\) returns (\d+) and Tail\(d\) returns (\d+)$")]
  public void ScenarioValues(string sum, string word, string cast, string tail) {
    PrototypeCall[] calls = [new("Sum", [SpanFixtures.D, 4, 4]), new("Word", [SpanFixtures.D]), new("CastXor", [SpanFixtures.D, 8]), new("Tail", [SpanFixtures.D])];
    string[] expected = [sum, word, cast, tail];
    Assert.That(this.Generated(calls).Select(Result), Is.EqualTo(expected), this._generated.Text);
    Assert.That(calls.Select(call => Result(SpanFixtures.DotNet("Spans", call))), Is.EqualTo(expected));
  }

  [Then(@"^Sum\(d, 13, 4\) and Sum\(d, -1, 2\) trap with " + StepText.Quoted + @" and Sum\(d, 16, 0\) returns (\d+)$")]
  public void ScenarioTraps(string trap, string empty) {
    PrototypeCall[] calls = [new("Sum", [SpanFixtures.D, 13, 4]), new("Sum", [SpanFixtures.D, -1, 2]), new("Sum", [SpanFixtures.D, 16, 0])];
    var trapped = PrototypeFixture.Trap(Enum.Parse<IrTrap>(trap));
    string[] expected = [trapped, trapped, empty];
    Assert.That(this.Generated(calls).Select(Result), Is.EqualTo(expected));
    Assert.That(calls.Select(call => Result(SpanFixtures.DotNet("Spans", call))), Is.EqualTo(expected));
  }

  /// <summary>The differential of the issue: every call of the fixture, the boundaries and the draws of the seed 2026 included.</summary>
  [Then("^every call of the fixture computes what .NET computes$")]
  public void EveryCall() {
    var calls = SpanFixtures.Calls().ToList();
    Assert.That(this.Generated(calls), Is.EqualTo(calls.Select(call => SpanFixtures.DotNet("Spans", call)).ToList()), this._generated.Text);
  }

  // ---------------------------------------------------------------- JavaScript to C#

  /// <summary>What node computes of the original JavaScript fixture.</summary>
  private IReadOnlyList<string> Original(IReadOnlyList<PrototypeCall> calls)
    => Node(scenario.Workspace.Resolve(scenario.Source), ProfileCase.Default, calls);

  [Then(@"^word\(d\) returns (\d+) and tail\(d\) returns (\d+) as in node$")]
  public void WordAndTail(string word, string tail) {
    PrototypeCall[] calls = [new("word", [SpanFixtures.D]), new("tail", [SpanFixtures.D])];
    string[] expected = [word, tail];
    Assert.That(this.Generated(calls).Select(Result), Is.EqualTo(expected));
    Assert.That(this.Original(calls).Select(Result), Is.EqualTo(expected));
  }

  [Then("^lengths\\(d\\) returns " + StepText.Quoted + ", although .NET's Slice\\(13, 4\\) on 16 bytes throws$")]
  public void Lengths(string lengths) {
    PrototypeCall[] calls = [new("lengths", [SpanFixtures.D])];
    Assert.That(Result(this.Generated(calls).Single()), Is.EqualTo($"[{lengths}]"));
    Assert.That(Result(this.Original(calls).Single()), Is.EqualTo($"[{lengths}]"));
    Assert.That(() => SpanFixtures.D.AsSpan().Slice(13, 4).ToArray(), Throws.TypeOf<ArgumentOutOfRangeException>());
  }

  [Then("^sliceVsSub\\(d\\) returns " + StepText.Quoted + ": slice copies, subarray aliases$")]
  public void SliceVersusSubarray(string values) {
    PrototypeCall[] calls = [new("sliceVsSub", [SpanFixtures.D])];
    Assert.That(Result(this.Generated(calls).Single()), Is.EqualTo($"[{values}]"));
    Assert.That(Result(this.Original(calls).Single()), Is.EqualTo($"[{values}]"));
  }

  // ---------------------------------------------------------------- Adler-32

  private string _javaScriptFixture = "";

  [Given("^the fixtures " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void Fixtures(string csharp, string javaScript) {
    foreach (var fixture in new[] { csharp, javaScript })
      scenario.Workspace.Copy(Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype", fixture.Replace('/', Path.DirectorySeparatorChar)), fixture);

    scenario.Source = csharp;
    this._javaScriptFixture = javaScript;
  }

  [Then("^Adler-32 of \"Wikipedia\" inside a larger buffer is 0x([0-9A-F]+) in .NET, in node and in every profile of both backends$")]
  public void Adler32(string hex) {
    var expected = Convert.ToUInt32(hex, 16).ToString(System.Globalization.CultureInfo.InvariantCulture);
    PrototypeCall[] csharp = [new("Compute", [SpanFixtures.Wikipedia, 5, 9])];
    PrototypeCall[] javaScript = [new("compute", [SpanFixtures.Wikipedia, 5, 9])];
    Assert.That(Result(SpanFixtures.DotNet("Adler32Span", csharp[0])), Is.EqualTo(expected));
    Assert.That(Result(Node(scenario.Workspace.Resolve(this._javaScriptFixture), ProfileCase.Default, javaScript).Single()), Is.EqualTo(expected));
    Sweep.Each(TargetCase.All, target => {
      foreach (var (source, calls) in new[] { (scenario.Source, csharp), (this._javaScriptFixture, javaScript) }) {
        if (target.CSharp is { } profile) {
          var (_, text) = scenario.Translate(source, Language(source), "csharp", "--profile", profile.Name);
          Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
          Assert.That(Result(SpanFixtures.Outcome(GeneratedCSharp.Compile(text, true).Method(calls[0].Method), calls[0])), Is.EqualTo(expected), $"{source} in {profile.Name}");
        } else {
          var generated = scenario.Translate(source, Language(source), "javascript", target.JavaScript!.Arguments);
          Assert.That(Result(Node(generated.Path, target.JavaScript, calls).Single()), Is.EqualTo(expected), $"{source} in {target}");
        }
      }
    });
  }

  // ---------------------------------------------------------------- one method

  /// <summary>Writes a C# method into the class <c>Snippet</c>, public and static, with the namespaces of spans.</summary>
  private void Method(string method)
    => scenario.Script("Snippet.cs", $"using System; using System.Runtime.InteropServices; public static class Snippet {{ public static {method} }}");

  [Given("^the C# method that copies d\\[0..8\\] of the bytes 0 to 9 to index 2$")]
  public void OverlappingCopy() {
    var fixture = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype", "spans", "Spans.cs");
    scenario.Workspace.Copy(fixture, "spans/Spans.cs");
    scenario.Source = "spans/Spans.cs";
  }

  [Then("^the array is " + StepText.Quoted + " in .NET, in the interpreter, in node at es5 without typed arrays and at es2022, and in C# for net2.0 and net10.0$")]
  public void MovesLikeMemmove(string elements) {
    var expected = $"[{elements}]";
    var call = new PrototypeCall("Overlap", []);
    Assert.That(SpanFixtures.DotNet("Spans", call), Is.EqualTo(expected));
    Assert.That(PrototypeFixture.InterpretedOutcome(SpanFixtures.Imported("Spans"), call), Is.EqualTo(expected));
    foreach (var level in new[] { "es5", "es2022" }) {
      var generated = scenario.Translate(scenario.Source, "csharp", "javascript", Level(level).Arguments);
      Assert.That(Node(generated.Path, Level(level), [call]).Single(), Is.EqualTo(expected), level);
    }

    foreach (var profile in new[] { "net2.0", "net10.0" }) {
      var (_, text) = scenario.Translate(scenario.Source, "csharp", "csharp", "--profile", profile);
      Assert.That(SpanFixtures.Outcome(GeneratedCSharp.Compile(text, true).Method("Overlap"), call), Is.EqualTo(expected), profile);
    }
  }

  [Given("^the static C# method " + StepText.Quoted + "$")]
  public void CSharpMethod(string method) => this.Method(StepText.Unescape(method));

  [Then("^it returns true in .NET and in every profile of both backends$")]
  public void ReturnsTrueEverywhere() {
    var call = new PrototypeCall("F", []);
    var source = File.ReadAllText(scenario.Workspace.Resolve(scenario.Source));
    Assert.That(SpanFixtures.Outcome(GeneratedCSharp.Compile(source, true).Method("F"), call), Is.EqualTo("true"));
    Sweep.Each(TargetCase.All, target => {
      if (target.CSharp is { } profile) {
        var (_, text) = scenario.Translate(scenario.Source, "csharp", "csharp", "--profile", profile.Name);
        Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
        Assert.That(SpanFixtures.Outcome(GeneratedCSharp.Compile(text, true).Method("F"), call), Is.EqualTo("true"), profile.Name);
      } else {
        var generated = scenario.Translate(scenario.Source, "csharp", "javascript", target.JavaScript!.Arguments);
        Assert.That(Node(generated.Path, target.JavaScript, [call]).Single(), Is.EqualTo("true"), target.ToString());
      }
    });
  }

  [When("^it is translated to JavaScript with " + StepText.Quoted + "$")]
  public void TranslatedToJavaScript(string options) => this.TranslatedWith("--to javascript " + options);

  [Then("^the output reads through a DataView with getUint32\\(…, true\\) and constructs no Uint32Array$")]
  public void ReadsThroughADataView() {
    Assert.That(this._generated.Text, Does.Contain(".getUint32(0, true)").And.Contain("new DataView(").And.Not.Contain("Uint32Array"));
  }

  [Then(@"^F\(d\) returns (\d+), as in \.NET$")]
  public void ReturnsAsInDotNet(string value) {
    var call = new PrototypeCall("F", [SpanFixtures.D]);
    var source = File.ReadAllText(scenario.Workspace.Resolve(scenario.Source));
    Assert.That(Result(SpanFixtures.Outcome(GeneratedCSharp.Compile(source, true).Method("F"), call)), Is.EqualTo(value));
    Assert.That(Result(this.Generated([call]).Single()), Is.EqualTo(value));
  }

  // ---------------------------------------------------------------- refusals

  [Given("^the JavaScript function " + StepText.Quoted + " with b a Uint8Array$")]
  public void JavaScriptFunction(string function)
    => scenario.Script("f.mjs", "/**\n * @param {uint8[]} b\n * @returns {uint32}\n */\n" + StepText.Unescape(function) + "\n");

  [When("^I run the translate command$")]
  public void TranslateCommand() {
    var (from, to) = Language(scenario.Source) == "csharp" ? ("csharp", "javascript") : ("javascript", "csharp");
    scenario.Polypiler.Run("translate", scenario.Source, "--from", from, "--to", to, "--output", "out");
  }

  [Then(@"^the exit code is (\d+) and the output contains " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void Refused(int exitCode, string code, string subject) {
    Assert.That(scenario.Log.Last.ExitCode, Is.EqualTo(exitCode), scenario.Log.Last.Output);
    Assert.That(scenario.Log.Last.Output, Does.Contain(code).And.Contain(subject));
    Assert.That(Directory.Exists(scenario.Workspace.Resolve("out")), Is.False);
  }

}
