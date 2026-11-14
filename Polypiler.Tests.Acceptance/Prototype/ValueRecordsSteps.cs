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
/// The steps of <c>tests/features/prototype/value-records.feature</c>. The expected outcome of a call
/// is never computed here: it is what .NET computes of the original C# fixture, built into this
/// assembly, or what <c>node</c> computes of the original JavaScript.
/// </summary>
[Binding, Scope(Feature = "Value records")]
public sealed class ValueRecordsSteps(PrototypeScenario scenario) {

  private (string Path, string Text) _generated;
  private ProfileCase? _profile;
  private CSharpProfile? _csharp;

  /// <summary>The profile of a level in the module format the level starts with: an ES module, or CommonJS at ECMAScript 5.</summary>
  private static ProfileCase Level(string level) => ProfileCase.All.First(profile => profile.Level == level && !profile.TypedArrays && profile.Module == (level == "es5" ? "cjs" : "esm"));

  private static string DotNet(string className, PrototypeCall call) => PrototypeFixture.InvokedOutcome(RecordFixtures.Compiled(className, call.Method), call);

  /// <summary>The class of the module, which names the file the command wrote.</summary>
  private string ClassName => Path.GetFileNameWithoutExtension(this._generated.Path);

  private static IReadOnlyList<string> Node((string Path, string Text) generated, ProfileCase profile, IReadOnlyList<PrototypeCall> calls)
    => Toolchain.NodeOutcomes(Path.GetDirectoryName(generated.Path)!, Path.GetFileName(generated.Path), calls, true, profile.Mode, profile.Global);

  /// <summary>Translates the fixture with the options of the step, which name the target.</summary>
  [When("^it is translated with " + StepText.Quoted + "$")]
  public void TranslatedWith(string options) {
    var arguments = CommandLine.Split(options).ToList();
    var to = arguments[arguments.IndexOf("--to") + 1];
    var name = arguments[arguments.IndexOf("--profile") + 1];
    var from = Path.GetExtension(scenario.Source) == ".cs" ? "csharp" : "javascript";
    if (to == "javascript") {
      this._profile = Level(name);
      this._generated = scenario.Translate(scenario.Source, from, to, this._profile.Arguments);
      Assert.That(GeneratedJavaScript.Problems(this._generated.Text, this._profile.Parsed), Is.Empty, this._generated.Text);
    } else {
      this._csharp = CSharpProfile.Parse(name);
      this._generated = scenario.Translate(scenario.Source, from, to, "--profile", name);
    }
  }

  /// <summary>A method of the module computes the same in node as in .NET.</summary>
  [Then(@"^(\w+)\(\) returns (\d+) in node, as in \.NET$")]
  public void ReturnsInNodeAsInDotNet(string method, string expected) {
    var call = new PrototypeCall(method, []);
    Assert.That(Node(this._generated, this._profile!, [call]).Single(), Is.EqualTo(expected));
    Assert.That(DotNet(this.ClassName, call), Is.EqualTo(expected));
  }

  /// <summary>The generated C# compiles for its profile at the profile's language version.</summary>
  [Then(@"^the generated file compiles against the reference assemblies of (\S+) at its language version without diagnostics$")]
  public void CompilesForTheProfile(string name) {
    Assert.That(this._csharp!.Name, Is.EqualTo(name));
    Assert.That(GeneratedCSharp.ComplaintsFor(this._generated.Text, this._csharp.Name, this._csharp.LanguageVersionName), Is.Empty, this._generated.Text);
  }

  /// <summary>Equal and unequal amounts compare as such.</summary>
  [Then(@"^Same\((\d+), (\d+)\) returns true and Same\((\d+), (\d+)\) returns false$")]
  public void SameCompares(long a, long b, long c, long d) {
    var build = GeneratedCSharp.Compile(this._generated.Text, true);
    Assert.That(build.Method("Same").Invoke(null, [a, b]), Is.True);
    Assert.That(build.Method("Same").Invoke(null, [c, d]), Is.False);
  }

  /// <summary>Two records whose doubles are NaN and zeros of either sign are equal, as a record struct's equality has it.</summary>
  [Then(@"^NaNEqual\(\) returns true, because P2\(NaN, 0\.0\) equals P2\(NaN, -0\.0\)$")]
  public void NaNEqual() => Assert.That(GeneratedCSharp.Compile(this._generated.Text, true).Method("NaNEqual").Invoke(null, []), Is.True);

  /// <summary>The struct is declared in the form of the profile.</summary>
  [Then("^the declaration of Money is " + StepText.Quoted + "$")]
  public void DeclarationIs(string form) => Assert.That(this._generated.Text, Does.Contain("\n" + StepText.Unescape(form)));

  /// <summary>The output declares the struct.</summary>
  [Then("^the output declares " + StepText.Quoted + "$")]
  public void OutputDeclares(string declaration) => Assert.That(this._generated.Text, Does.Contain(declaration));

  /// <summary>The generated C# computes what node computes of the original JavaScript.</summary>
  [Then(@"^midX\(1, 2, 4, 6\) returns 2\.5 and dotOf\(1, 2, 3, 4\) returns 11, as in node$")]
  public void ComputesAsInNode() {
    PrototypeCall[] calls = [new("midX", [1.0, 2.0, 4.0, 6.0]), new("dotOf", [1.0, 2.0, 3.0, 4.0])];
    string[] expected = [PrototypeFixture.Show(2.5), "11"];
    scenario.Workspace.Copy(scenario.Workspace.Resolve(scenario.Source), "geometry.mjs");
    Assert.That(Toolchain.NodeOutcomes(scenario.Workspace.Path, "geometry.mjs", calls, true), Is.EqualTo(expected));
    Assert.That(calls.Select(GeneratedCSharp.Compile(this._generated.Text, true).Outcome), Is.EqualTo(expected));
  }

  /// <summary>The 128-bit addition carries into the upper half in .NET, in the interpreter, in every C# profile and in node at the levels of the step.</summary>
  [Then(@"^Add of \(Lo 0xFFFFFFFFFFFFFFFF, Hi 0\) and \(Lo 1, Hi 0\) returns Lo 0 and Hi 1 in \.NET, in the interpreter, in every C# profile and in node at (es\d+), (es\d+) and (es\d+)$")]
  public void AdditionCarries(string first, string second, string third) {
    PrototypeCall[] calls = [
      new("AddLo", [ulong.MaxValue, 0UL, 1UL, 0UL]) { Returns = typeof(ulong) },
      new("AddHi", [ulong.MaxValue, 0UL, 1UL, 0UL]) { Returns = typeof(ulong) }
    ];
    string[] expected = ["0", "1"];
    Assert.That(calls.Select(call => DotNet("Wide128", call)), Is.EqualTo(expected));
    Assert.That(calls.Select(call => PrototypeFixture.InterpretedOutcome(RecordFixtures.Imported("U128"), call)), Is.EqualTo(expected));
    var targets = CSharpProfile.All.Select(profile => new TargetCase(profile, null)).Concat(new[] { first, second, third }.Select(level => new TargetCase(null, Level(level)))).ToList();
    Sweep.Each(targets, target => {
      if (target.CSharp is { } profile) {
        var (_, text) = scenario.Translate(scenario.Source, "csharp", "csharp", "--profile", profile.Name);
        Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
        Assert.That(calls.Select(GeneratedCSharp.Compile(text, true).Outcome), Is.EqualTo(expected), profile.Name);
      } else {
        var generated = scenario.Translate(scenario.Source, "csharp", "javascript", target.JavaScript!.Arguments);
        Assert.That(Node(generated, target.JavaScript, calls), Is.EqualTo(expected), target.ToString());
      }
    });
  }

  /// <summary>Writes a JavaScript function into a module of the scenario.</summary>
  [Given("^the JavaScript function " + StepText.Quoted + "$")]
  public void JavaScriptFunction(string function) => scenario.Script("f.js", StepText.Unescape(function));

  /// <summary>Runs the translate command on the JavaScript source, to C#.</summary>
  [When("^I run the translate command to C#$")]
  public void TranslateCommand() => scenario.Polypiler.Run("translate", scenario.Source, "--from", "javascript", "--to", "csharp", "--output", "out");

  /// <summary>The diagnostic names the construct, and nothing was written.</summary>
  [Then("^the output contains " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void OutputContains(string code, string subject) {
    Assert.That(scenario.Log.Last.Output, Does.Contain(code).And.Contain(subject));
    Assert.That(Directory.Exists(scenario.Workspace.Resolve("out")), Is.False);
  }

  /// <summary>The command was refused with the exit code and a diagnostic naming the construct.</summary>
  [Then(@"^the exit code is (\d+) and the output contains " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void Refused(int exitCode, string code, string subject) {
    Assert.That(scenario.Log.Last.ExitCode, Is.EqualTo(exitCode), scenario.Log.Last.Output);
    this.OutputContains(code, subject);
  }

}
