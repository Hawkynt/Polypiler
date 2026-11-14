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
/// The steps of <c>tests/features/ir/ir-module-state.feature</c>: the state of a module, called
/// in sequence in one fresh load of the generated file - one <c>node</c> import, one load of the
/// compiled C# - against the values Marsaglia's generators produce.
/// </summary>
[Binding, Scope(Feature = "Mutable module state")]
public sealed class ModuleStateSteps(PrototypeScenario scenario) {

  private (string Path, string Text) _generated;
  private ProfileCase? _profile;
  private CSharpProfile? _csharp;

  /// <summary>Whether the generated file came from JavaScript, whose functions are named in camel case.</summary>
  private bool FromJavaScript => Path.GetExtension(scenario.Source) == ".js";

  /// <summary>Translates the fixture with the options of the step, which name the target.</summary>
  [When("^it is translated with " + StepText.Quoted + "$")]
  public void TranslatedWith(string options) {
    var arguments = CommandLine.Split(options).ToList();
    var to = arguments[arguments.IndexOf("--to") + 1];
    arguments.RemoveRange(arguments.IndexOf("--to"), 2);
    var from = this.FromJavaScript ? "javascript" : "csharp";
    if (to == "javascript") {
      this._profile = ProfileCase.Of([.. arguments]);
      this._generated = scenario.Translate(scenario.Source, from, to, this._profile.Arguments);
      Assert.That(GeneratedJavaScript.Problems(this._generated.Text, this._profile.Parsed), Is.Empty, this._generated.Text);
    } else {
      this.ToCSharp(from, arguments[arguments.IndexOf("--profile") + 1]);
    }
  }

  /// <summary>Translates the fixture to C# for a profile.</summary>
  [When("^it is translated to C# with " + StepText.Quoted + "$")]
  public void TranslatedToCSharp(string options) {
    var arguments = CommandLine.Split(options).ToList();
    this.ToCSharp(this.FromJavaScript ? "javascript" : "csharp", arguments[arguments.IndexOf("--profile") + 1]);
  }

  private void ToCSharp(string from, string profile) {
    this._csharp = CSharpProfile.Parse(profile);
    this._generated = scenario.Translate(scenario.Source, from, "csharp", "--profile", profile);
    Assert.That(GeneratedCSharp.ComplaintsFor(this._generated.Text, this._csharp.Name, this._csharp.LanguageVersionName), Is.Empty, this._generated.Text);
  }

  /// <summary>The outcomes of calls, one after the other, in one fresh load of the generated file.</summary>
  private IReadOnlyList<string> Outcomes(IReadOnlyList<PrototypeCall> calls) {
    if (this._profile is { } profile)
      return Toolchain.NodeOutcomes(Path.GetDirectoryName(this._generated.Path)!, Path.GetFileName(this._generated.Path), calls, false, profile.Mode, profile.Global);

    return StateFixtures.Compiled(this._generated.Text, calls);
  }

  private PrototypeCall Call(string method, params object[] arguments) {
    var call = new PrototypeCall(method, arguments);
    return this.FromJavaScript ? StateFixtures.Script(call) : call;
  }

  /// <summary>Three numbers of xorshift32, and the count of the numbers given.</summary>
  [Then(@"^three calls of Next return (\d+), (\d+) and (\d+), and Calls then returns (\d+)$")]
  public void NextAndCalls(string first, string second, string third, string calls)
    => Assert.That(this.Outcomes([this.Call("Next"), this.Call("Next"), this.Call("Next"), this.Call("Calls")]), Is.EqualTo(new[] { first, second, third, calls }), this._generated.Text);

  /// <summary>Three numbers of xorshift64.</summary>
  [Then(@"^three calls of Next return (\d+), (\d+) and (\d+)$")]
  public void Next(string first, string second, string third) {
    var next = this.Call("Next") with { Returns = typeof(ulong) };
    Assert.That(this.Outcomes([next, next, next]), Is.EqualTo(new[] { first, second, third }), this._generated.Text);
  }

  /// <summary>Three additions to the histogram.</summary>
  [Then(@"^add\((-?\d+)\), add\((-?\d+)\) and add\((-?\d+)\) return (\d+), (\d+) and (\d+)$")]
  public void Add(int a, int b, int c, string first, string second, string third)
    => Assert.That(this.Outcomes([new("add", [a]), new("add", [b]), new("add", [c])]), Is.EqualTo(new[] { first, second, third }), this._generated.Text);

  /// <summary>Writes a C# class into the workspace as the source.</summary>
  [Given("^the C# class " + StepText.Quoted + "$")]
  public void CSharpClass(string text) => scenario.Script("G.cs", StepText.Unescape(text));

  /// <summary>Writes a JavaScript module into the workspace as the source.</summary>
  [Given("^the JavaScript module " + StepText.Quoted + "$")]
  public void JavaScriptModule(string text) => scenario.Script("module.mjs", StepText.Unescape(text));

  /// <summary>Runs the translate command on the C# source, to JavaScript.</summary>
  [When("^I run the translate command to JavaScript$")]
  public void ToJavaScript() => scenario.Polypiler.Run("translate", scenario.Source, "--from", "csharp", "--to", "javascript", "--output", "out");

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

}
