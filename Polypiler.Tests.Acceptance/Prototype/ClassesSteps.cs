using System.IO;
using System.Linq;
using NUnit.Framework;
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

  /// <summary>Translates the fixture with the options of the step, which name the target.</summary>
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
