using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The differential rows of <c>tests/features/prototype/sequences.feature</c> (#1327) that need
/// <c>node</c>: the module of iterators at every JavaScript level computes in node what the interpreter
/// computes - the values and the event traces of laziness, restarting and closing.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd")]
[Layer("core")]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase), Parallelizable(ParallelScope.All)]
public sealed class SequenceTests {

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  /// <summary>The calls of the module of iterators, each with the type it returns.</summary>
  private static List<PrototypeCall> Calls()
    => [.. SequenceModules.Calls().Select(call => call with { Returns = call.Method == "Longs" ? typeof(long) : typeof(int) })];

  /// <summary>Test design 1 to 8: the module of iterators computes in node at every level what the interpreter computes.</summary>
  [TestCaseSource(typeof(ProfileCase), nameof(ProfileCase.All))]
  public void IteratorModule_RunsInNodeLikeTheInterpreter(ProfileCase profile) {
    var module = SequenceModules.Module();
    var calls = Calls();
    var text = JavaScriptEmitter.Emit(module, profile.Parsed).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(text, profile.Parsed), Is.Empty, text);
    File.WriteAllText(Path.Combine(this._directory, module.Name + profile.Parsed.Extension), text);
    var outcomes = Toolchain.NodeOutcomes(this._directory, module.Name + profile.Parsed.Extension, calls, true, profile.Mode, profile.Global);
    Assert.That(outcomes, Is.EqualTo(calls.Select(call => PrototypeFixture.InterpretedOutcome(module, call))), $"{profile}\n{text}");
  }

}
