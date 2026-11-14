using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The steps of <c>tests/features/lang/js/be-js-scopes.feature</c> (#1343): IR that declares a local
/// while one of its name is visible, emitted for every JavaScript profile and called in <c>node</c>,
/// against the interpreter and the C# compiler's build of the C# the same IR becomes.
/// </summary>
[Binding, Scope(Feature = "Shadowed locals keep their meaning in generated JavaScript")]
public sealed class JavaScriptScopesSteps(PrototypeScenario scenario) {

  private static readonly bool[] _overflowChecking = [false, true];

  private ShadowingModules.Case? _case;
  private readonly List<(ProfileCase Profile, string Path, string Content)> _files = [];

  /// <summary>The reproduction of the issue.</summary>
  [Given(@"^the IR function f\(n: i32\) with t: f64 = 0\.5 and an inner block declaring t': i32 = n \* 2 and returning t' \+ \(t \+ t\)$")]
  public void Reproduction() => this._case = ShadowingModules.InnerAfterOuter();

  /// <summary>A case of <see cref="ShadowingModules"/> by its name.</summary>
  [Given("^the IR case " + StepText.Quoted + "$")]
  public void IrCase(string name) => this._case = ShadowingModules.All.Single(@case => @case.Name == name);

  /// <summary>Emits the module for every profile, each into a directory of its own, where node finds it under a name of its own.</summary>
  [When("^it is emitted for every JavaScript profile$")]
  public void EmittedForEveryProfile() {
    Assert.That(IrVerifier.Verify(this._case!.Module), Is.Empty);
    var index = 0;
    foreach (var profile in ProfileCase.All) {
      var file = JavaScriptEmitter.Emit(this._case.Module, profile.Parsed).Single();
      var path = Path.Combine("p" + index++, file.Path);
      scenario.Workspace.Write(path, file.Content);
      this._files.Add((profile, path, file.Content));
    }
  }

  /// <summary>What node returns for a call in every profile.</summary>
  private IEnumerable<(ProfileCase Profile, string Content, string Outcome)> Node(PrototypeCall call)
    => this._files.Select(file => (file.Profile, file.Content, Toolchain.NodeOutcomes(scenario.Workspace.Path, file.Path, [call], true, file.Profile.Mode, file.Profile.Global).Single()));

  /// <summary>A call returns the value of the step in node, in every profile.</summary>
  [Then(@"^f\((-?\d+)\) returns (-?\d+) in node for every profile$")]
  public void ReturnsInEveryProfile(int argument, string value) {
    var call = new PrototypeCall("F", [argument]);
    Assert.That(PrototypeFixture.InterpretedOutcome(this._case!.Module, call), Is.EqualTo(value), "interpreter");
    foreach (var (profile, content, outcome) in this.Node(call))
      Assert.That(outcome, Is.EqualTo(value), $"{profile}:\n{content}");
  }

  /// <summary>Every call of the case returns the same in node for every profile, the interpreter and the C# build.</summary>
  [Then("^every call returns in node for every profile what the interpreter and the C# compiler's build return$")]
  public void EveryCallAgrees() {
    var csharp = CSharpEmitter.Emit(this._case!.Module, CSharpProfile.Parse("net10.0")).Single().Content;
    Assert.That(GeneratedCSharp.ComplaintsFor(csharp, "net10.0", "14"), Is.Empty, csharp);
    foreach (var (argument, result) in this._case.Calls) {
      var call = new PrototypeCall("F", [argument]);
      Assert.That(PrototypeFixture.InterpretedOutcome(this._case.Module, call), Is.EqualTo(result), $"interpreter {call}");
      foreach (var checking in _overflowChecking)
        Assert.That(GeneratedCSharp.Compile(csharp, checking).Outcome(call), Is.EqualTo(result), $"C# {call}, checked {checking}");

      foreach (var (profile, content, outcome) in this.Node(call))
        Assert.That(outcome, Is.EqualTo(result), $"{call} in {profile}:\n{content}");
    }
  }

  /// <summary>Every profile declares <c>t</c> twice and no renamed <c>t</c>.</summary>
  [Then("^the generated JavaScript declares t in both blocks and renames neither$")]
  public void SiblingsKeepTheirNames() {
    foreach (var (profile, _, content) in this._files) {
      Assert.That(Regex.Matches(content, @"(?m)^ +(?:let|var) t = "), Has.Count.EqualTo(2), $"{profile}:\n{content}");
      Assert.That(content, Does.Not.Contain("t_1").And.Not.Contain("t$1"), profile.ToString());
    }
  }

}
