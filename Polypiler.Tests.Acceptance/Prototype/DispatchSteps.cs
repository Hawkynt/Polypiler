using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The steps of <c>tests/features/ir/ir-dispatch.feature</c>. The expected value is never computed
/// here: it is what .NET computes of the original C# fixture, built into this assembly, or what
/// <c>node</c> computes of the original JavaScript.
/// </summary>
[Binding, Scope(Feature = "Inheritance, interfaces and virtual dispatch")]
public sealed class DispatchSteps(PrototypeScenario scenario) {

  private (string Path, string Text) _generated;
  private ProfileCase? _profile;
  private CSharpProfile? _csharp;
  private readonly List<string> _fixtures = [];

  /// <summary>The profile of a level in the module format the level starts with: an ES module, or CommonJS at ECMAScript 5.</summary>
  private static ProfileCase Level(string level) => ProfileCase.All.First(profile => profile.Level == level && !profile.TypedArrays && profile.Module == (level == "es5" ? "cjs" : "esm"));

  /// <summary>A fixture of the folder with the scenario's own description of it.</summary>
  [Given("^the fixture " + StepText.Quoted + " (?:with|in which) .+$")]
  public void DescribedFixture(string path) {
    scenario.Workspace.Copy(DispatchFixtures.PathOf(Path.GetFileName(path)), path);
    scenario.Source = path;
  }

  /// <summary>Copies two fixtures into the workspace.</summary>
  [Given("^the fixtures " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void Fixtures(string first, string second) {
    foreach (var path in new[] { first, second }) {
      scenario.Workspace.Copy(DispatchFixtures.PathOf(Path.GetFileName(path)), path);
      this._fixtures.Add(path);
    }
  }

  /// <summary>Translates the fixture with the options of the step, which name the target and its profile.</summary>
  [When("^it is translated with " + StepText.Quoted + "$")]
  public void TranslatedWith(string options) {
    var arguments = CommandLine.Split(options).ToList();
    var to = arguments[arguments.IndexOf("--to") + 1];
    var name = arguments[arguments.IndexOf("--profile") + 1];
    var from = Path.GetExtension(scenario.Source) == ".cs" ? "csharp" : "javascript";
    if (to == "csharp") {
      this._csharp = CSharpProfile.Parse(name);
      this._generated = scenario.Translate(scenario.Source, from, to, "--profile", this._csharp.Name);
      return;
    }

    this._profile = Level(name);
    this._generated = scenario.Translate(scenario.Source, from, "javascript", this._profile.Arguments);
    Assert.That(GeneratedJavaScript.Problems(this._generated.Text, this._profile.Parsed), Is.Empty, this._generated.Text);
  }

  /// <summary>The generated file holds a form of the lowering table.</summary>
  [Then("^the output contains " + StepText.Quoted + "$")]
  public void OutputContains(string form) => Assert.That(this._generated.Text, Does.Contain(form));

  /// <summary>The shapes in node sum as they do in .NET.</summary>
  [Then(@"^in node the sum of Twice\(\) over Rect\((\d+), (\d+)\) and Circle\((\d+)\) is (\d+) and the sum of Sides\(\) is (\d+), as in \.NET$")]
  public void ShapesInNode(double width, double height, double radius, string twice, string sides) {
    List<PrototypeCall> calls = [new("SumTwice", [width, height, radius]), new("SumSides", [width, height, radius])];
    var dotNet = calls.Select(call => PrototypeFixture.InvokedOutcome(DispatchFixtures.Compiled("ShapesDemo", call.Method), call)).ToList();
    Assert.That(dotNet, Is.EqualTo(new[] { twice, sides }), ".NET");
    var outcomes = Toolchain.NodeOutcomes(Path.GetDirectoryName(this._generated.Path)!, Path.GetFileName(this._generated.Path), calls, false, this._profile!.Mode, this._profile.Global);
    Assert.That(outcomes, Is.EqualTo(dotNet), this._generated.Text);
  }

  /// <summary>The generated C# compiles for its profile at the profile's language version.</summary>
  [Then(@"^the generated file compiles against the reference assemblies of (\S+) at its language version without diagnostics$")]
  public void CompilesForTheProfile(string name) {
    Assert.That(this._csharp!.Name, Is.EqualTo(name));
    Assert.That(GeneratedCSharp.ComplaintsFor(this._generated.Text, this._csharp.Name, this._csharp.LanguageVersionName), Is.Empty, this._generated.Text);
  }

  /// <summary>The method of the base class is virtual where the derived class overrides it, and the override says so.</summary>
  [Then("^the output contains " + StepText.Quoted + " in Shape and " + StepText.Quoted + " in Rect$")]
  public void VirtualAndOverride(string virtualForm, string overrideForm) {
    var text = this._generated.Text;
    var shape = text.IndexOf("class Shape", StringComparison.Ordinal);
    var rect = text.IndexOf("class Rect", StringComparison.Ordinal);
    Assert.That(text.IndexOf(virtualForm, shape, StringComparison.Ordinal), Is.InRange(shape, rect), text);
    Assert.That(text.IndexOf(overrideForm, rect, StringComparison.Ordinal), Is.GreaterThan(rect), text);
  }

  /// <summary>The JavaScript fixture itself in node: the calls run on a copy of it as an ES module, which exports what a script declares.</summary>
  private List<string> Original(string path, IReadOnlyList<PrototypeCall> calls) {
    var source = File.ReadAllText(scenario.Workspace.Resolve(path));
    var copy = Path.Combine(scenario.Workspace.Resolve("."), "original-" + Guid.NewGuid().ToString("N") + ".mjs");
    File.WriteAllText(copy, source.Contains("export ", StringComparison.Ordinal) ? source : source + $"\nexport {{ {string.Join(", ", calls.Select(call => call.Method).Distinct())} }};\n");
    return [.. Toolchain.NodeOutcomes(Path.GetDirectoryName(copy)!, Path.GetFileName(copy), calls)];
  }

  /// <summary>The shapes translated to C# sum as the original does in node.</summary>
  [Then(@"^the sum of twice\(\) is (\d+) and the sum of sides\(\) is (\d+), as in node$")]
  public void SumsAsInNode(string twice, string sides) {
    List<PrototypeCall> calls = [new("sumTwice", [2.0, 3.0, 1.0]), new("sumSides", [2.0, 3.0, 1.0])];
    Assert.That(this.Original(scenario.Source, calls), Is.EqualTo(new[] { twice, sides }), "node");
    Assert.That(calls.Select(GeneratedCSharp.Compile(this._generated.Text, false).Outcome), Is.EqualTo(new[] { twice, sides }), this._generated.Text);
  }

  /// <summary>
  /// The outcome of calls of a fixture in its own runtime - .NET for C#, node for JavaScript - which
  /// is what is expected of the interpreter, of every C# profile and of node at ECMAScript 5 and 2022.
  /// </summary>
  private void Everywhere(string path, IReadOnlyList<PrototypeCall> calls, IReadOnlyList<string> expected, IReadOnlyList<ProfileCase> levels) {
    var csharp = path.EndsWith(".cs", StringComparison.Ordinal);
    var from = csharp ? "csharp" : "javascript";
    if (csharp) {
      var className = Path.GetFileNameWithoutExtension(path) + "Demo";
      Assert.That(calls.Select(call => PrototypeFixture.InvokedOutcome(DispatchFixtures.Compiled(className, call.Method), call)), Is.EqualTo(expected), ".NET");
      Assert.That(calls.Select(call => PrototypeFixture.InterpretedOutcome(DispatchFixtures.Imported(Path.GetFileNameWithoutExtension(path)), call)), Is.EqualTo(expected), "the interpreter");
    } else {
      Assert.That(this.Original(path, calls), Is.EqualTo(expected), "node");
      var module = JavaScriptFrontend.Import(path, File.ReadAllText(scenario.Workspace.Resolve(path))).Module!;
      Assert.That(calls.Select(call => PrototypeFixture.InterpretedOutcome(module, call)), Is.EqualTo(expected), "the interpreter");
    }

    foreach (var profile in CSharpProfile.All) {
      var generated = scenario.Translate(path, from, "csharp", "--profile", profile.Name);
      Assert.That(GeneratedCSharp.ComplaintsFor(generated.Text, profile.Name, profile.LanguageVersionName), Is.Empty, generated.Text);
      Assert.That(calls.Select(GeneratedCSharp.Compile(generated.Text, false).Outcome), Is.EqualTo(expected), $"{path} for {profile.Name}");
    }

    foreach (var profile in levels) {
      var generated = scenario.Translate(path, from, "javascript", profile.Arguments);
      Assert.That(GeneratedJavaScript.Problems(generated.Text, profile.Parsed), Is.Empty, generated.Text);
      Assert.That(Toolchain.NodeOutcomes(Path.GetDirectoryName(generated.Path)!, Path.GetFileName(generated.Path), calls, false, profile.Mode, profile.Global),
        Is.EqualTo(expected), $"{path} in {profile}");
    }
  }

  /// <summary>The override reaches the base implementation through base or super, in both fixtures, everywhere.</summary>
  [Then(@"^new Dog\(\)\.Describe\(\) returns (\d+) and new Animal\(\)\.Describe\(\) returns (\d+) in \.NET, node, the interpreter, every C# profile and node at es5 and es2022$")]
  public void BaseCallsEverywhere(string dog, string animal) {
    foreach (var path in this._fixtures) {
      var csharp = path.EndsWith(".cs", StringComparison.Ordinal);
      List<PrototypeCall> calls = csharp ? [new("DogDescribe", []), new("AnimalDescribe", [])] : [new("dogDescribe", []), new("animalDescribe", [])];
      this.Everywhere(path, calls, [dog, animal], [Level("es5"), Level("es2022")]);
    }
  }

  /// <summary>The type tests of a Circle held as a Shape, in every implementation and every profile of both backends.</summary>
  [Then(@"^""o is Rect"" is (false|true), ""o is IShape"" is (false|true) and ""o as Rect"" is null in \.NET, the interpreter and every profile of both backends$")]
  public void TypeTestsEverywhere(string isRect, string isShape)
    => this.Everywhere(scenario.Source, [new("IsRect", [1.0]), new("IsShape", [1.0]), new("AsRectIsNull", [1.0])], [isRect, isShape, "true"], ProfileCase.All);

  /// <summary>The cast of a Circle to Rect fails in every implementation, each in its own way.</summary>
  [Then(@"^""\(Rect\)o"" throws InvalidCastException in \.NET and every C# profile, traps with InvalidCast in the interpreter and throws a TypeError in node at es5 and es2022$")]
  public void CastFailsEverywhere() {
    var call = new PrototypeCall("CastToRect", [1.0]);
    var trap = PrototypeFixture.Trap(IrTrap.InvalidCast);
    Assert.That(PrototypeFixture.InvokedOutcome(DispatchFixtures.Compiled("ShapesDemo", call.Method), call), Is.EqualTo(trap), ".NET");
    Assert.That(PrototypeFixture.InterpretedOutcome(DispatchFixtures.Imported("Shapes"), call), Is.EqualTo(trap), "the interpreter");
    foreach (var profile in CSharpProfile.All) {
      var generated = scenario.Translate(scenario.Source, "csharp", "csharp", "--profile", profile.Name);
      Assert.That(GeneratedCSharp.Compile(generated.Text, false).Outcome(call), Is.EqualTo(trap), profile.Name);
    }

    foreach (var level in new[] { "es5", "es2022" }) {
      var profile = Level(level);
      var generated = scenario.Translate(scenario.Source, "csharp", "javascript", profile.Arguments);
      Assert.That(ClassesSteps.Node(generated.Path, profile.Module == "esm", "CastToRect", "try { CastToRect(1); console.log('none'); } catch (error) { console.log(error.constructor.name + ' ' + error.message); }"),
        Is.EqualTo("TypeError invalid cast"), level);
    }
  }

  /// <summary>Runs the translate command on the source, to JavaScript.</summary>
  [When("^I run the translate command to JavaScript$")]
  public void ToJavaScript() => scenario.Polypiler.Run("translate", scenario.Source, "--from", "csharp", "--to", "javascript", "--output", "out");

  /// <summary>Runs the translate command on the source, to C#.</summary>
  [When("^I run the translate command to C#$")]
  public void ToCSharp() => scenario.Polypiler.Run("translate", scenario.Source, "--from", "javascript", "--to", "csharp", "--output", "out");

  /// <summary>The output names the diagnostic and the construct, and nothing was written.</summary>
  [Then("^the output contains " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void OutputNames(string code, string subject) {
    Assert.That(scenario.Log.Last.Output, Does.Contain(code).And.Contain(subject));
    Assert.That(Directory.Exists(scenario.Workspace.Resolve("out")), Is.False);
  }

}
