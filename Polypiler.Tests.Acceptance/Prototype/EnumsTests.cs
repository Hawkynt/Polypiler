using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// Test design 1, 2, 3, 4 and 6 of #1318 with <c>node</c>: a module of every operation on enums over
/// every underlying type, written at every level, module format and typed-array setting, computes for
/// every call what the interpreter computes - the text of every flags value among them, the boundaries
/// of every underlying type and every class of text a parse distinguishes.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd")]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase), Parallelizable(ParallelScope.All)]
public sealed class EnumsTests {

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  /// <summary>Every operation at every level runs in node like the interpreter.</summary>
  [TestCaseSource(typeof(ProfileCase), nameof(ProfileCase.All))]
  public void EveryOperation_RunsInNodeLikeTheInterpreter(ProfileCase profile) {
    var module = EnumFixtures.Module;
    var parsed = profile.Parsed;
    Assert.That(JavaScriptEmitter.Check(module, parsed), Is.Empty);
    var file = module.Name + parsed.Extension;
    var text = JavaScriptEmitter.Emit(module, parsed).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(text, parsed), Is.Empty, text);
    File.WriteAllText(Path.Combine(this._directory, file), text);
    var calls = EnumFixtures.Calls().ToList();
    var outcomes = Toolchain.NodeOutcomes(this._directory, file, calls, false, profile.Mode, profile.Global);
    var mismatches = calls.Select((call, index) => (call, Expected: PrototypeFixture.InterpretedOutcome(module, call), Actual: outcomes[index]))
      .Where(result => result.Expected != result.Actual).Select(result => $"{result.call}: {result.Actual}, the interpreter {result.Expected}").ToList();
    Assert.That(mismatches.Take(10), Is.Empty, $"{mismatches.Count} of {calls.Count} in {profile}\n{text}");
  }

}
