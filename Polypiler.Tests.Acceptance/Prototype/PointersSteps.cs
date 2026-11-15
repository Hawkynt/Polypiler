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
/// The steps of <c>tests/features/ir/ir-pointers.feature</c> (#1331). An expected value is never computed here:
/// it is what .NET computes of the fixture built into this assembly with AllowUnsafeBlocks, or the trap the
/// interpreter reports where C# is undefined. A trap of a pointer is compared by its kind.
/// </summary>
[Binding, Scope(Feature = "Bounded pointers from unsafe C#")]
public sealed class PointersSteps(PrototypeScenario scenario) {

  private (string Path, string Text) _generated;
  private ProfileCase? _profile;

  /// <summary>The profile of a level in the module format the level starts with: an ES module, or CommonJS at ECMAScript 5 without typed arrays.</summary>
  private static ProfileCase Level(string level) => ProfileCase.All.First(profile => profile.Level == level && !profile.TypedArrays && profile.Module == (level == "es5" ? "cjs" : "esm"));

  /// <summary>What node computes of a generated module for calls.</summary>
  private static IReadOnlyList<string> Node(string path, ProfileCase profile, IReadOnlyList<PrototypeCall> calls)
    => Toolchain.NodeOutcomes(Path.GetDirectoryName(path)!, Path.GetFileName(path), calls, true, profile.Mode, profile.Global);

  /// <summary>What the generated file computes for calls: the compiled C#, or node for JavaScript.</summary>
  private IReadOnlyList<string> Generated(IReadOnlyList<PrototypeCall> calls) {
    if (this._profile is { } profile)
      return Node(this._generated.Path, profile, calls);

    var build = GeneratedCSharp.Compile(this._generated.Text, false);
    return [.. calls.Select(build.Outcome)];
  }

  /// <summary>The result of an outcome without the arrays the call was given.</summary>
  private static string Result(string outcome) => outcome.Split(" | ")[0];

  private static int[] Ints(int count) => new int[count];

  [When("^it is translated with " + StepText.Quoted + "$")]
  public void TranslatedWith(string options) {
    var arguments = CommandLine.Split(options).ToList();
    var to = arguments[arguments.IndexOf("--to") + 1];
    var name = arguments[arguments.IndexOf("--profile") + 1];
    if (to == "javascript") {
      this._profile = Level(name);
      this._generated = scenario.Translate(scenario.Source, "csharp", to, this._profile.Arguments);
      Assert.That(GeneratedJavaScript.Problems(this._generated.Text, this._profile.Parsed), Is.Empty, this._generated.Text);
    } else {
      var profile = CSharpProfile.Parse(name);
      this._generated = scenario.Translate(scenario.Source, "csharp", to, "--profile", name);
      Assert.That(GeneratedCSharp.ComplaintsFor(this._generated.Text, name, profile.LanguageVersionName), Is.Empty, this._generated.Text);
    }
  }

  [When("^" + StepText.Quoted + " is translated with " + StepText.Quoted + "$")]
  public void FixtureTranslatedWith(string fixture, string options) {
    scenario.Workspace.Copy(PointerFixtures.PathOf(Path.GetFileName(fixture)), fixture);
    scenario.Source = fixture;
    this.TranslatedWith(options);
  }

  [Then(@"^Checksum\(d\) returns (\d+), Word\(d, 1\) returns (\d+), Distance\(new int\[10\], 2, 7\) returns (-?\d+) and Distance\(new int\[10\], 7, 2\) returns (-?\d+)$")]
  public void KernelValues(string checksum, string word, string forward, string backward) {
    PrototypeCall[] calls = [new("Checksum", [PointerFixtures.D]), new("Word", [PointerFixtures.D, 1]), new("Distance", [Ints(10), 2, 7]), new("Distance", [Ints(10), 7, 2])];
    string[] expected = [checksum, word, forward, backward];
    Assert.That(this.Generated(calls).Select(Result), Is.EqualTo(expected), this._generated.Text);
    Assert.That(calls.Select(call => Result(PointerFixtures.DotNet("Pointers", call))), Is.EqualTo(expected));
  }

  [Then(@"^Stack\(3\) returns (\d+), Local\(\) returns (\d+), EmptyIsNull\(\) returns (true|false) and Bits\(\) returns ([\d.]+)$")]
  public void OriginValues(string stack, string local, string empty, string bits) {
    PrototypeCall[] calls = [new("Stack", [3]), new("Local", []), new("EmptyIsNull", []), new("Bits", [])];
    string[] expected = [stack, local, empty, PrototypeFixture.Show(double.Parse(bits, System.Globalization.CultureInfo.InvariantCulture))];
    Assert.That(this.Generated(calls).Select(Result), Is.EqualTo(expected), this._generated.Text);
    Assert.That(calls.Select(call => Result(PointerFixtures.DotNet("Pointers", call))), Is.EqualTo(expected));
  }

  [Then("^the class is declared " + StepText.Quoted + ", Word contains " + StepText.Quoted + " and the header names " + StepText.Quoted + "$")]
  public void DeclaredUnsafe(string modifier, string read, string option) {
    var text = this._generated.Text;
    Assert.That(text, Does.Contain($"public static {modifier} class Pointers"));
    Assert.That(Function(text, "Word"), Does.Contain(read));
    var header = string.Join('\n', text.Split('\n').TakeWhile(line => line.StartsWith("//", StringComparison.Ordinal)));
    Assert.That(header, Does.Contain(option));
  }

  [Then("^it compiles against the .NET Framework 2.0 reference assemblies at C# 2 with AllowUnsafeBlocks and without diagnostics$")]
  public void CompilesAtCSharp2()
    => Assert.That(GeneratedCSharp.ComplaintsFor(this._generated.Text, "net2.0", "2"), Is.Empty, this._generated.Text);

  [Then("^Word contains " + StepText.Quoted + " with " + StepText.Quoted + " and the output contains no " + StepText.Quoted + "$")]
  public void ReadsThroughADataView(string method, string little, string forbidden) {
    var word = Function(this._generated.Text, "Word");
    Assert.That(word, Does.Contain(method).And.Contain(little));
    Assert.That(this._generated.Text, Does.Not.Contain(forbidden));
  }

  /// <summary>The text of a function of a generated file, from its name to the brace that ends it.</summary>
  private static string Function(string text, string name) {
    var start = text.IndexOf(name + "(", StringComparison.Ordinal);
    var end = text.IndexOf("\n  }\n", start, StringComparison.Ordinal);
    var close = text.IndexOf("\n}\n", start, StringComparison.Ordinal);
    return text[start..(end < 0 || (close >= 0 && close < end) ? close : end)];
  }

  // ---------------------------------------------------------------- traps

  [Then(@"^F\(new int\[4\]\) traps with " + StepText.Quoted + " in the interpreter and in node at es5 and es2022$")]
  public void TrapsOutsideCSharp(string trap) {
    var expected = PrototypeFixture.Trap(Enum.Parse<IrTrap>(trap));
    var call = new PrototypeCall("F", [Ints(4)]);
    var module = PrototypeFixture.Import(scenario.Source, File.ReadAllText(scenario.Workspace.Resolve(scenario.Source)));
    Assert.That(PrototypeFixture.InterpretedOutcome(module, call), Is.EqualTo(expected));
    foreach (var level in new[] { "es5", "es2022" }) {
      var generated = scenario.Translate(scenario.Source, "csharp", "javascript", Level(level).Arguments);
      Assert.That(Node(generated.Path, Level(level), [call]).Single(), Is.EqualTo(expected), level);
    }
  }

  [Then("^every in-bounds call of the fixture computes what .NET computes in every profile of both backends$")]
  public void EveryCallEverywhere() {
    var calls = PointerFixtures.Calls().ToList();
    var expected = calls.Select(call => PointerFixtures.DotNet("Pointers", call)).ToList();
    Assert.That(calls.Select(call => PrototypeFixture.InterpretedOutcome(PointerFixtures.Imported("Pointers"), call)), Is.EqualTo(expected));
    Sweep.Each(TargetCase.All, target => {
      if (target.CSharp is { } profile) {
        var (_, text) = scenario.Translate(scenario.Source, "csharp", "csharp", "--profile", profile.Name);
        Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
        var build = GeneratedCSharp.Compile(text, false);
        Assert.That(calls.Select(build.Outcome), Is.EqualTo(expected), profile.Name);
      } else {
        var generated = scenario.Translate(scenario.Source, "csharp", "javascript", target.JavaScript!.Arguments);
        Assert.That(GeneratedJavaScript.Problems(generated.Text, target.JavaScript.Parsed), Is.Empty, generated.Text);
        Assert.That(Node(generated.Path, target.JavaScript, calls), Is.EqualTo(expected), target.ToString());
      }
    });
  }

  /// <summary>
  /// The traps the IR defines where C# is undefined, in every JavaScript profile. The null pointer throws the
  /// engine's class of a null reference, a <c>TypeError</c>, which the driver of the calls reports as no trap: a
  /// script of its own tells it apart.
  /// </summary>
  [Then("^every trapping call of the fixture traps as in the interpreter in every JavaScript profile$")]
  public void EveryTrapEverywhere() {
    var module = PointerFixtures.Imported("Pointers");
    var traps = PointerFixtures.Traps().ToList();
    foreach (var (call, trap) in traps)
      Assert.That(PrototypeFixture.InterpretedOutcome(module, call), Is.EqualTo(PrototypeFixture.Trap(trap)), call.Method);

    var ranged = traps.Where(entry => entry.Trap != IrTrap.NullReference).ToList();
    Sweep.Each(TargetCase.JavaScriptProfiles, target => {
      var profile = target.JavaScript!;
      var generated = scenario.Translate(scenario.Source, "csharp", "javascript", profile.Arguments);
      Assert.That(Node(generated.Path, profile, [.. ranged.Select(entry => entry.Call)]), Is.EqualTo(ranged.Select(entry => PrototypeFixture.Trap(entry.Trap))), target.ToString());
    });

    foreach (var level in new[] { "es5", "es2022" }) {
      var generated = scenario.Translate(scenario.Source, "csharp", "javascript", Level(level).Arguments);
      var load = level == "es5" ? $"require('./{Path.GetFileName(generated.Path)}')" : $"await import('./{Path.GetFileName(generated.Path)}')";
      var empty = level == "es5" ? "[]" : "new Int32Array(0)";
      var script = Path.Combine(Path.GetDirectoryName(generated.Path)!, "null.mjs");
      File.WriteAllText(script, (level == "es5" ? "import { createRequire } from 'module'; const require = createRequire(import.meta.url);\n" : "")
        + $"const m = {load};\ntry {{ m.At({empty}, 0); console.log('none'); }} catch (e) {{ console.log(e instanceof TypeError ? 'TypeError: ' + e.message : 'other: ' + e); }}\n");
      var (exitCode, output) = Toolchain.RunNode(Path.GetDirectoryName(script)!, script);
      Assert.That((exitCode, output.Trim()), Is.EqualTo((0, "TypeError: null reference")), level);
    }
  }

  // ---------------------------------------------------------------- FNV-1a

  [Then("^Hash\\(\"a\"\\) is 0x([0-9A-F]+) in .NET, in the interpreter and in every profile of both backends$")]
  public void Fnv1a(string hex) {
    var expected = Convert.ToUInt32(hex, 16).ToString(System.Globalization.CultureInfo.InvariantCulture);
    PrototypeCall[] calls = [new("Hash", ["a"u8.ToArray()])];
    Assert.That(Result(PointerFixtures.DotNet("Fnv1aPointer", calls[0])), Is.EqualTo(expected));
    Assert.That(Result(PrototypeFixture.InterpretedOutcome(PointerFixtures.Imported("Fnv1aPointer"), calls[0])), Is.EqualTo(expected));
    Sweep.Each(TargetCase.All, target => {
      if (target.CSharp is { } profile) {
        var (_, text) = scenario.Translate(scenario.Source, "csharp", "csharp", "--profile", profile.Name);
        Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
        Assert.That(Result(GeneratedCSharp.Compile(text, false).Outcome(calls[0])), Is.EqualTo(expected), profile.Name);
      } else {
        var generated = scenario.Translate(scenario.Source, "csharp", "javascript", target.JavaScript!.Arguments);
        Assert.That(Result(Node(generated.Path, target.JavaScript, calls).Single()), Is.EqualTo(expected), target.ToString());
      }
    });
  }

  // ---------------------------------------------------------------- refusals

  [When("^I run the translate command$")]
  public void TranslateCommand() => scenario.Polypiler.Run("translate", scenario.Source, "--from", "csharp", "--to", "javascript", "--output", "out");

  [Then(@"^the exit code is (\d+) and the output contains " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void Refused(int exitCode, string code, string subject) {
    Assert.That(scenario.Log.Last.ExitCode, Is.EqualTo(exitCode), scenario.Log.Last.Output);
    Assert.That(scenario.Log.Last.Output, Does.Contain(code).And.Contain(subject));
    Assert.That(Directory.Exists(scenario.Workspace.Resolve("out")), Is.False);
  }

}
