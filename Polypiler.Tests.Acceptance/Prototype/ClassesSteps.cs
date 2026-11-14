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
/// The steps of <c>tests/features/ir/ir-classes.feature</c>. The expected value is never computed
/// here: it is what .NET computes of the original C# fixture, built into this assembly, or what
/// <c>node</c> computes of the original JavaScript.
/// </summary>
[Binding, Scope(Feature = "Sealed classes and instances")]
public sealed class ClassesSteps(PrototypeScenario scenario) {

  private (string Path, string Text) _generated;
  private ProfileCase? _profile;

  /// <summary>The profile of a level in the module format the level starts with: an ES module, or CommonJS at ECMAScript 5.</summary>
  private static ProfileCase Level(string level) => ProfileCase.All.First(profile => profile.Level == level && !profile.TypedArrays && profile.Module == (level == "es5" ? "cjs" : "esm"));

  /// <summary>Translates the fixture to JavaScript with the options of the step.</summary>
  [When("^it is translated with " + StepText.Quoted + "$")]
  public void TranslatedWith(string options) {
    var arguments = CommandLine.Split(options).ToList();
    var name = arguments[arguments.IndexOf("--profile") + 1];
    var from = Path.GetExtension(scenario.Source) == ".cs" ? "csharp" : "javascript";
    this._profile = Level(name);
    this._generated = scenario.Translate(scenario.Source, from, "javascript", this._profile.Arguments);
    Assert.That(GeneratedJavaScript.Problems(this._generated.Text, this._profile.Parsed), Is.Empty, this._generated.Text);
  }

  /// <summary>The generated file holds a form of the lowering table.</summary>
  [Then("^the output contains " + StepText.Quoted + "$")]
  public void OutputContains(string form) => Assert.That(this._generated.Text, Does.Contain(form));

  /// <summary>The class the generated file exports behaves in node as the C# class does in .NET.</summary>
  [Then(@"^in node: new Counter\((\d+)\), Step = (\d+), Next\(\) twice returns (\d+) and Count is (\d+), as in \.NET$")]
  public void CounterInNode(int start, int step, int next, int count) {
    var type = ClassFixtures.Type("Counter");
    var counter = System.Activator.CreateInstance(type, [start])!;
    type.GetProperty("Step")!.SetValue(counter, step);
    type.GetMethod("Next")!.Invoke(counter, []);
    var dotNet = $"{type.GetMethod("Next")!.Invoke(counter, [])} {type.GetProperty("Count")!.GetValue(counter)}";
    Assert.That(dotNet, Is.EqualTo($"{next} {count}"));
    Assert.That(Node(this._generated.Path, this._profile!.Module == "esm", "Counter", $"const c = new Counter({start}); c.Step = {step}; c.Next(); console.log(c.Next() + ' ' + c.Count);"),
      Is.EqualTo(dotNet), this._generated.Text);
  }

  private readonly List<string> _fixtures = [];

  /// <summary>Copies two fixtures into the workspace.</summary>
  [Given("^the fixtures " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void Fixtures(string first, string second) {
    foreach (var path in new[] { first, second }) {
      scenario.Workspace.Copy(ClassFixtures.PathOf(Path.GetFileName(path)), path);
      this._fixtures.Add(path);
    }
  }

  /// <summary>
  /// The outcome of calls of a C# fixture's static class in .NET, which is what is expected of the
  /// interpreter, of every C# profile and of node at ECMAScript 5 and 2022.
  /// </summary>
  private void Everywhere(string path, string className, IReadOnlyList<PrototypeCall> calls, IReadOnlyList<string> expected) {
    var file = Path.GetFileNameWithoutExtension(path);
    Assert.That(calls.Select(call => PrototypeFixture.InvokedOutcome(ClassFixtures.Compiled(className, call.Method), call)), Is.EqualTo(expected), ".NET");
    Assert.That(calls.Select(call => PrototypeFixture.InterpretedOutcome(ClassFixtures.Imported(file), call)), Is.EqualTo(expected), "the interpreter");
    foreach (var profile in CSharpProfile.All) {
      var generated = scenario.Translate(path, "csharp", "csharp", "--profile", profile.Name);
      Assert.That(GeneratedCSharp.ComplaintsFor(generated.Text, profile.Name, profile.LanguageVersionName), Is.Empty, generated.Text);
      var build = GeneratedCSharp.Compile(generated.Text, false);
      Assert.That(calls.Select(build.Outcome), Is.EqualTo(expected), profile.Name);
    }

    foreach (var level in new[] { "es5", "es2022" }) {
      var profile = Level(level);
      var generated = scenario.Translate(path, "csharp", "javascript", profile.Arguments);
      Assert.That(Toolchain.NodeOutcomes(Path.GetDirectoryName(generated.Path)!, Path.GetFileName(generated.Path), calls, false, profile.Mode, profile.Global), Is.EqualTo(expected), level);
    }
  }

  /// <summary>Copies of a reference name one object; two objects made alike are two.</summary>
  [Then(@"^in \.NET, the interpreter, every C# profile and node at es5 and es2022: after b = a and b\.Next\(\) on new Counter\(0\), a\.Count is (\d+), a and b are the same object, and two new Counter\(0\) are not$")]
  public void Aliasing(string count)
    => this.Everywhere(this._fixtures.Single(path => path.EndsWith("Counter.cs", StringComparison.Ordinal)), "CounterDemo",
      [new("Aliased", []), new("SameObject", []), new("TwoObjects", [])], [count, "true", "false"]);

  /// <summary>Two records of equal fields are equal and still two objects.</summary>
  [Then(@"^new Range\((\d+), (\d+)\) equals new Range\(\1, \2\) by value but not by reference, and its Length is (\d+)$")]
  public void RecordEquality(int lo, int hi, string length)
    => this.Everywhere(this._fixtures.Single(path => path.EndsWith("Range.cs", StringComparison.Ordinal)), "RangeDemo",
      [new("ValueEqual", [lo, hi]), new("SameReference", [lo, hi]), new("Length", [lo, hi])], ["true", "false", length]);

  /// <summary>Writes the method into a static class next to the class Counter of the fixture.</summary>
  [Given("^the C# method " + StepText.Quoted + " next to the class Counter$")]
  public void NextToCounter(string method) {
    var fixture = File.ReadAllText(ClassFixtures.PathOf("Counter.cs"));
    var start = fixture.IndexOf("public sealed class Counter", StringComparison.Ordinal);
    var end = fixture.IndexOf("public static class CounterDemo", StringComparison.Ordinal);
    scenario.Script("Snippet.cs", "#nullable disable\n" + fixture[start..end] + "public static class Snippet {\n  " + StepText.Unescape(method) + "\n}\n");
  }

  /// <summary>The call of F traps in every implementation, as each reports a null reference.</summary>
  [Then(@"^\.NET throws NullReferenceException, the interpreter traps with NullReference, every C# profile throws NullReferenceException and node at es5 and es2022 throws a TypeError$")]
  public void NullTrapsEverywhere() {
    var trap = PrototypeFixture.Trap(IrTrap.NullReference);
    var call = new PrototypeCall("F", []);
    var source = File.ReadAllText(scenario.Workspace.Resolve(scenario.Source));
    Assert.That(GeneratedCSharp.Compile(source, false).Outcome(call), Is.EqualTo(trap), ".NET");
    var module = PrototypeFixture.Import("Snippet.cs", source);
    Assert.That(PrototypeFixture.InterpretedOutcome(module, call), Is.EqualTo(trap), "the interpreter");
    foreach (var profile in CSharpProfile.All) {
      var generated = scenario.Translate(scenario.Source, "csharp", "csharp", "--profile", profile.Name);
      Assert.That(GeneratedCSharp.Compile(generated.Text, false).Outcome(call), Is.EqualTo(trap), profile.Name);
    }

    foreach (var level in new[] { "es5", "es2022" }) {
      var profile = Level(level);
      var generated = scenario.Translate(scenario.Source, "csharp", "javascript", profile.Arguments);
      Assert.That(Node(generated.Path, profile.Module == "esm", "F", "try { F(); console.log('none'); } catch (error) { console.log(error.constructor.name); }"), Is.EqualTo("TypeError"), level);
    }
  }

  /// <summary>What node prints of a script that imports one name of a generated file.</summary>
  public static string Node(string file, bool module, string name, string script) {
    var directory = Path.GetDirectoryName(file)!;
    var load = module
      ? $"import {{ {name} }} from {Quoted("./" + Path.GetFileName(file))};"
      : $"const {{ {name} }} = require({Quoted("./" + Path.GetFileName(file))});";
    var driver = Path.Combine(directory, "driver-" + System.Guid.NewGuid().ToString("N") + (module ? ".mjs" : ".cjs"));
    File.WriteAllText(driver, load + "\n" + script + "\n");
    var (exitCode, output) = Toolchain.RunNode(directory, driver);
    Assert.That(exitCode, Is.Zero, output);
    return output.Trim();
  }

  private static string Quoted(string text) => "\"" + text.Replace("\\", "\\\\", System.StringComparison.Ordinal).Replace("\"", "\\\"", System.StringComparison.Ordinal) + "\"";

}
