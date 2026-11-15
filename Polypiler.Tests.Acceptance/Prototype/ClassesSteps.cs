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

  private CSharpProfile? _csharp;

  /// <summary>Translates the fixture with the options of the step, which name the target; C# without a profile is its default.</summary>
  [When("^it is translated with " + StepText.Quoted + "$")]
  public void TranslatedWith(string options) {
    var arguments = CommandLine.Split(options).ToList();
    var to = arguments[arguments.IndexOf("--to") + 1];
    var name = arguments.IndexOf("--profile") is var at and >= 0 ? arguments[at + 1] : null;
    var from = Path.GetExtension(scenario.Source) == ".cs" ? "csharp" : "javascript";
    if (to == "csharp") {
      this._csharp = name is null ? CSharpProfile.Default : CSharpProfile.Parse(name);
      this._generated = scenario.Translate(scenario.Source, from, to, "--profile", this._csharp.Name);
      return;
    }

    this._profile = Level(name!);
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

  /// <summary>The generated C# compiles for its profile at the profile's language version.</summary>
  [Then(@"^the generated file compiles against the reference assemblies of (\S+) at its language version without diagnostics$")]
  public void CompilesForTheProfile(string name) {
    Assert.That(this._csharp!.Name, Is.EqualTo(name));
    Assert.That(GeneratedCSharp.ComplaintsFor(this._generated.Text, this._csharp.Name, this._csharp.LanguageVersionName), Is.Empty, this._generated.Text);
  }

  /// <summary>The JavaScript fixture itself in node: the script runs on a copy of it as an ES module.</summary>
  private string Original(string name, string script) {
    var copy = Path.Combine(scenario.Workspace.Resolve("."), "original-" + Guid.NewGuid().ToString("N") + ".mjs");
    var source = File.ReadAllText(scenario.Workspace.Resolve(scenario.Source));
    // A script declares its functions without export; the copy exports the one the script imports.
    File.WriteAllText(copy, source.Contains("export ", StringComparison.Ordinal) ? source : source + $"\nexport {{ {name} }};\n");
    return Node(copy, true, name, script);
  }

  /// <summary>The C# class translated from JavaScript does what the original does in node.</summary>
  [Then(@"^new Counter\((\d+)\), step = (\d+), next\(\) twice returns (\d+), count is (\d+) and Counter\.created is (\d+), as in node$")]
  public void CounterAsInNode(int start, int step, string next, string count, string created) {
    var expected = $"{next} {count} {created}";
    Assert.That(this.Original("Counter", $"const c = new Counter({start}); c.step = {step}; c.next(); console.log(c.next() + ' ' + c.count + ' ' + Counter.created);"), Is.EqualTo(expected));
    var type = GeneratedCSharp.Compile(this._generated.Text, false).Type("Counter");
    var counter = Activator.CreateInstance(type, [(double)start])!;
    type.GetField("step")!.SetValue(counter, (double)step);
    type.GetMethod("next")!.Invoke(counter, []);
    var last = type.GetMethod("next")!.Invoke(counter, []);
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    Assert.That(string.Join(' ', ((double)last!).ToString(inv), ((double)type.GetProperty("count")!.GetValue(counter)!).ToString(inv), ((double)type.GetField("created")!.GetValue(null)!).ToString(inv)),
      Is.EqualTo(expected), this._generated.Text);
  }

  /// <summary>The generated file declares the class.</summary>
  [Then("^the output declares " + StepText.Quoted + "$")]
  public void Declares(string declaration) => Assert.That(this._generated.Text, Does.Contain(declaration));

  /// <summary>The constructor function translated to C# sums as the original does in node.</summary>
  [Then(@"^new Acc\((\d+)\), add\((\d+)\), add\((\d+)\) returns (\d+), as in node$")]
  public void AccumulatorAsInNode(int start, int first, int second, string sum) {
    Assert.That(this.Original("Acc", $"const a = new Acc({start}); a.add({first}); console.log(a.add({second}));"), Is.EqualTo(sum));
    var result = GeneratedCSharp.Compile(this._generated.Text, false).Method("sum").Invoke(null, [(double)start, (double)first, (double)second]);
    Assert.That(((double)result!).ToString(System.Globalization.CultureInfo.InvariantCulture), Is.EqualTo(sum), this._generated.Text);
  }

  /// <summary>Writes a JavaScript module into the workspace as the source.</summary>
  [Given("^the JavaScript module " + StepText.Quoted + "$")]
  public void JavaScriptModule(string text) => scenario.Script("module.mjs", StepText.Unescape(text));

  /// <summary>Runs the translate command on the JavaScript source, to C#.</summary>
  [When("^I run the translate command to C#$")]
  public void ToCSharp() => scenario.Polypiler.Run("translate", scenario.Source, "--from", "javascript", "--to", "csharp", "--output", "out");

  /// <summary>The command was refused with the exit code and a diagnostic naming the construct, and wrote nothing.</summary>
  [Then(@"^the exit code is (\d+) and the output contains " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void Refused(int exitCode, string code, string subject) {
    var result = scenario.Log.Last;
    Assert.That(result.ExitCode, Is.EqualTo(exitCode), result.Output);
    Assert.That(result.Output, Does.Contain(code).And.Contain(subject));
    Assert.That(Directory.Exists(scenario.Workspace.Resolve("out")), Is.False);
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

  /// <summary>
  /// Adler-32 of "Wikipedia" from the C# and the JavaScript fixture alike: in .NET and node, in the
  /// interpreter, and translated into every profile of both backends from both.
  /// </summary>
  [Then(@"^Update with the bytes of ""Wikipedia"" followed by Value returns (\d+) in \.NET, node, the interpreter and every profile of both backends in both directions$")]
  public void AdlerEverywhere(string expected) {
    foreach (var path in this._fixtures) {
      var csharp = path.EndsWith(".cs", StringComparison.Ordinal);
      var from = csharp ? "csharp" : "javascript";
      var call = new PrototypeCall(csharp ? "OfWikipedia" : "ofWikipedia", []);
      if (csharp) {
        Assert.That(PrototypeFixture.InvokedOutcome(ClassFixtures.Compiled("Adler32Demo", call.Method), call), Is.EqualTo(expected), ".NET");
        Assert.That(PrototypeFixture.InterpretedOutcome(ClassFixtures.Imported("Adler32"), call), Is.EqualTo(expected), "the interpreter of the C#");
      } else {
        var copy = Path.Combine(Path.GetDirectoryName(scenario.Workspace.Resolve(path))!, "original.mjs");
        File.Copy(scenario.Workspace.Resolve(path), copy);
        Assert.That(Toolchain.NodeOutcomes(Path.GetDirectoryName(copy)!, "original.mjs", [call]).Single(), Is.EqualTo(expected), "node");
        var imported = Polypiler.Core.Frontends.JavaScript.JavaScriptFrontend.Import(path, File.ReadAllText(scenario.Workspace.Resolve(path))).Module!;
        Assert.That(PrototypeFixture.InterpretedOutcome(imported, call), Is.EqualTo(expected), "the interpreter of the JavaScript");
      }

      foreach (var profile in CSharpProfile.All) {
        var generated = scenario.Translate(path, from, "csharp", "--profile", profile.Name);
        Assert.That(GeneratedCSharp.ComplaintsFor(generated.Text, profile.Name, profile.LanguageVersionName), Is.Empty, generated.Text);
        Assert.That(GeneratedCSharp.Compile(generated.Text, false).Outcome(call), Is.EqualTo(expected), $"{path} for {profile.Name}");
      }

      foreach (var profile in ProfileCase.All) {
        var generated = scenario.Translate(path, from, "javascript", profile.Arguments);
        Assert.That(GeneratedJavaScript.Problems(generated.Text, profile.Parsed), Is.Empty, generated.Text);
        Assert.That(Toolchain.NodeOutcomes(Path.GetDirectoryName(generated.Path)!, Path.GetFileName(generated.Path), [call], false, profile.Mode, profile.Global).Single(),
          Is.EqualTo(expected), $"{path} in {profile}");
      }
    }
  }

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
