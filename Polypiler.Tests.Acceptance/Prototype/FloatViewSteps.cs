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
/// The steps of <c>tests/features/prototype/float-view.feature</c>: floating-point numbers read from
/// and written to byte buffers, compared with what .NET's compiled fixture and <c>node</c> on the
/// original JavaScript do.
/// </summary>
[Binding, Scope(Feature = "Floating-point numbers in byte buffers")]
public sealed class FloatViewSteps(PrototypeScenario scenario) {

  private static readonly bool[] _overflowChecking = [false, true];

  private (string Path, string Text) _generated;
  private ProfileCase? _profile;
  private GeneratedCSharp? _build;
  private byte[] _buffer = [];

  private string Directory => scenario.Workspace.Path;

  /// <summary>The fixture of a language in the workspace, under its own name.</summary>
  private string Copy(string fixture) {
    var name = Path.GetFileName(fixture);
    scenario.Workspace.Copy(Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype", fixture), name);
    return name;
  }

  /// <summary>A C# translation for a profile: compiled against the profile's reference assemblies, then built for this runtime.</summary>
  private GeneratedCSharp CSharp(string source, string from, CSharpProfile profile, bool checking = false) {
    var (_, text) = scenario.Translate(source, from, "csharp", "--profile", profile.Name);
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, $"{profile}:\n{text}");
    return GeneratedCSharp.Compile(text, checking);
  }

  /// <summary>What node does with a JavaScript translation for a profile.</summary>
  private static IReadOnlyList<string> Node((string Path, string Text) generated, ProfileCase profile, IReadOnlyList<PrototypeCall> calls) {
    Assert.That(GeneratedJavaScript.Problems(generated.Text, profile.Parsed), Is.Empty, profile.ToString());
    return Toolchain.NodeOutcomes(Path.GetDirectoryName(generated.Path)!, Path.GetFileName(generated.Path), calls, true, profile.Mode, profile.Global);
  }

  private IReadOnlyList<string> Node(string source, string from, ProfileCase profile, IReadOnlyList<PrototypeCall> calls)
    => Node(scenario.Translate(source, from, "javascript", profile.Arguments), profile, calls);

  /// <summary>Translates the fixture with the options of the step, which name the target.</summary>
  [When("^it is translated with " + StepText.Quoted + "$")]
  public void TranslatedWith(string options) {
    var arguments = CommandLine.Split(options).ToList();
    var to = arguments[arguments.IndexOf("--to") + 1];
    arguments.RemoveRange(arguments.IndexOf("--to"), 2);
    var from = Path.GetExtension(scenario.Source) == ".cs" ? "csharp" : "javascript";
    if (to == "javascript") {
      this._profile = ProfileCase.Of([.. arguments]);
      arguments = [.. this._profile.Arguments];
    }

    this._generated = scenario.Translate(scenario.Source, from, to, [.. arguments]);
  }

  /// <summary>The generated C# compiles against the reference assemblies of its profile, and is built for this runtime.</summary>
  [Then(@"^the generated file compiles against the reference assemblies of (\S+)$")]
  public void CompilesForTheProfile(string name) {
    var profile = CSharpProfile.Parse(name);
    Assert.That(GeneratedCSharp.ComplaintsFor(this._generated.Text, profile.Name, profile.LanguageVersionName), Is.Empty, $"{profile}:\n{this._generated.Text}");
    this._build = GeneratedCSharp.Compile(this._generated.Text, false);
  }

  /// <summary>A read of the translation has the value the step names, which is also what node has for the original.</summary>
  [Then(@"^(getF(32|64)(?:LE|BE)) of the bytes ((?:[0-9A-F]{2} ?)+) at index (\d+) returns (\S+), as node returns$")]
  public void ReadReturns(string method, string width, string bytes, int index, string value) {
    var call = new PrototypeCall(method, [Convert.FromHexString(bytes.Replace(" ", "")), index]) { Returns = width == "32" ? typeof(float) : typeof(double) };
    var number = double.Parse(value, CultureInfo.InvariantCulture);
    var expected = (width == "32" ? PrototypeFixture.Show((float)number) : PrototypeFixture.Show(number)) + " | " + FloatViewTests.Bytes(bytes);
    Assert.That(FloatViewTests.Original(this.Directory, [call]).Single(), Is.EqualTo(expected), "node");
    Assert.That(this._build!.Outcome(call), Is.EqualTo(expected));
  }

  /// <summary>A store of the translation in node leaves the bytes .NET's compiled fixture leaves.</summary>
  [Then(@"^PutF32LE\(bytes, 0, (\S+)\) leaves the bytes ((?:[0-9A-F]{2} ?)+) in node, as .NET leaves them$")]
  public void StoreLeaves(string value, string bytes) {
    var call = new PrototypeCall("PutF32LE", [new byte[4], 0, double.Parse(value, CultureInfo.InvariantCulture)]);
    var expected = $"{FloatViewTests.Bytes(bytes)} | {FloatViewTests.Bytes(bytes)}";
    Assert.That(PrototypeFixture.CompiledOutcome("float/FloatView", call), Is.EqualTo(expected), ".NET");
    Assert.That(Node(this._generated, this._profile!, [call]).Single(), Is.EqualTo(expected), this._profile!.ToString());
  }

  /// <summary>Negative zero stored big-endian keeps its sign bit in .NET, in node and in both fixtures translated for every profile of both backends.</summary>
  [Then("^PutF64BE\\(bytes, 0, -0\\.0\\) leaves 80 00 00 00 00 00 00 00 in .NET, in node and in every profile of both backends$")]
  public void NegativeZeroKeepsItsSign() {
    var expected = $"{FloatViewTests.Bytes("80 00 00 00 00 00 00 00")} | {FloatViewTests.Bytes("80 00 00 00 00 00 00 00")}";
    var call = new PrototypeCall("PutF64BE", [new byte[8], 0, -0.0]);
    var script = call with { Method = "putF64BE" };
    Assert.That(PrototypeFixture.CompiledOutcome("float/FloatView", call), Is.EqualTo(expected), ".NET");
    Assert.That(FloatViewTests.Original(this.Directory, [script]).Single(), Is.EqualTo(expected), "node");
    var csharp = this.Copy("float/FloatView.cs");
    var javascript = this.Copy("js/FloatView.js");
    Sweep.Each(TargetCase.All, target => {
      var outcomes = new Dictionary<string, string>();
      if (target.CSharp is { } profile) {
        outcomes[$"C# to {profile.Name}"] = this.CSharp(csharp, "csharp", profile, true).Outcome(call);
        outcomes[$"JavaScript to {profile.Name}"] = this.CSharp(javascript, "javascript", profile, true).Outcome(script);
      } else {
        outcomes[$"C# to {target}"] = this.Node(csharp, "csharp", target.JavaScript!, [call]).Single();
        outcomes[$"JavaScript to {target}"] = this.Node(javascript, "javascript", target.JavaScript!, [script]).Single();
      }

      Assert.That(outcomes.Where(run => run.Value != expected).Select(run => $"{run.Key}: {run.Value}"), Is.Empty);
    });
  }

  /// <summary>The buffer of the first scenario's fixture: twelve bytes.</summary>
  [Given(@"^the (\d+)-byte buffer of the first scenario$")]
  public void BufferOfTheFirstScenario(int length) {
    Assert.That(length, Is.EqualTo(FloatViewCases.Length));
    this._buffer = new byte[length];
  }

  /// <summary>A read past the end throws a RangeError in node, and the C# translation of the JavaScript fixture traps for every profile, with overflow checking off and on.</summary>
  [Then(@"^getF32LE at index (\d+) throws a RangeError in node, and the C# translation throws for every profile$")]
  public void ReadOutsideTraps(int index) {
    var call = new PrototypeCall("getF32LE", [this._buffer, index]) { Returns = typeof(float) };
    const string trap = "trap: index out of range";
    var script = scenario.Workspace.Write("outside.mjs",
      $"const view = new DataView(new Uint8Array({this._buffer.Length}).buffer); try {{ view.getFloat32({index}, true); console.log('read'); }} catch (error) {{ console.log(error.constructor.name); }}");
    var (exitCode, output) = Toolchain.RunNode(this.Directory, script);
    Assert.That((exitCode, output.Trim()), Is.EqualTo((0, "RangeError")));
    Assert.That(FloatViewTests.Original(this.Directory, [call]).Single(), Is.EqualTo(trap));
    var javascript = this.Copy("js/FloatView.js");
    Sweep.Each(TargetCase.CSharpProfiles, target => {
      foreach (var checking in _overflowChecking)
        Assert.That(this.CSharp(javascript, "javascript", target.CSharp!, checking).Outcome(call), Is.EqualTo(trap), target.ToString());
    });
  }

  /// <summary>Writes a JavaScript function into a module of the scenario.</summary>
  [Given("^the JavaScript function " + StepText.Quoted + "$")]
  public void JavaScriptFunction(string function) => scenario.Script("snippet.mjs", StepText.Unescape(function));

  /// <summary>Runs the translate command on the JavaScript source, to C#.</summary>
  [When("^I run the translate command$")]
  public void TranslateCommand() => scenario.Polypiler.Run("translate", scenario.Source, "--from", "javascript", "--to", "csharp", "--output", "out");

  /// <summary>The command was refused with a diagnostic naming the construct, and wrote nothing.</summary>
  [Then(@"^the exit code is (\d+) and the output contains " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void Refused(int exitCode, string code, string subject) {
    var result = scenario.Log.Last;
    Assert.That(result.ExitCode, Is.EqualTo(exitCode), result.Output);
    Assert.That(result.Output, Does.Contain(code).And.Contain(subject));
    Assert.That(System.IO.Directory.Exists(scenario.Workspace.Resolve("out")), Is.False);
  }

}
