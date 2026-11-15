using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
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

  /// <summary>Translates a file in process and returns the generated text; the time it took is reported.</summary>
  private string Translate(string source, string to, string generated, string output, params string[] further) {
    var directory = Path.Combine(this._directory, output);
    var clock = Stopwatch.StartNew();
    var (exitCode, text) = Toolchain.Polypiler(true, this._directory, ["translate", source, "--from", source.EndsWith(".cs", StringComparison.Ordinal) ? "csharp" : "javascript", "--to", to, "--output", directory, .. further]);
    TestContext.Out.WriteLine($"translate {Path.GetFileName(source)} --to {to} {string.Join(' ', further)}: {clock.ElapsedMilliseconds} ms");
    Assert.That(exitCode, Is.Zero, text);
    var written = Directory.GetFiles(directory).Single(path => Path.GetFileName(path) != "polypiler.manifest.json");
    Assert.That(Path.GetFileName(written), Is.EqualTo(generated));
    return File.ReadAllText(written);
  }

  /// <summary>Test design 1 to 7: every call of the C# fixture computes in node at every level what .NET computes.</summary>
  [TestCaseSource(typeof(ProfileCase), nameof(ProfileCase.All))]
  public void CSharpFixture_RunsInNodeLikeDotNet(ProfileCase profile) {
    var calls = SequenceFixtures.Calls().ToList();
    var text = this.Translate(SequenceFixtures.PathOf("Iterators.cs"), "javascript", "Iterators" + profile.Parsed.Extension, "out", profile.Arguments);
    Assert.That(GeneratedJavaScript.Problems(text, profile.Parsed), Is.Empty, text);
    var outcomes = Toolchain.NodeOutcomes(Path.Combine(this._directory, "out"), "Iterators" + profile.Parsed.Extension, calls, true, profile.Mode, profile.Global);
    Assert.That(outcomes, Is.EqualTo(calls.Select(call => PrototypeFixture.InvokedOutcome(SequenceFixtures.Compiled(call.Method), call))), $"{profile}\n{text}");
  }

  /// <summary>Test design 1 to 7: every call of the C# fixture computes in every C# profile what .NET computes.</summary>
  [Test]
  public void CSharpFixture_RunsInEveryCSharpProfileLikeDotNet() {
    var calls = SequenceFixtures.Calls().ToList();
    var expected = calls.Select(call => PrototypeFixture.InvokedOutcome(SequenceFixtures.Compiled(call.Method), call)).ToList();
    foreach (var profile in CSharpProfile.All) {
      var text = this.Translate(SequenceFixtures.PathOf("Iterators.cs"), "csharp", "Iterators.cs", profile.Name, "--profile", profile.Name);
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
      Assert.That(calls.Select(GeneratedCSharp.Compile(text, false).Outcome), Is.EqualTo(expected), $"{profile.Name}\n{text}");
    }
  }

  /// <summary>The calls of the JavaScript fixture and what node computes of the original.</summary>
  private (List<PrototypeCall> Calls, IReadOnlyList<string> Expected) Original() {
    var calls = SequenceFixtures.ScriptCalls().ToList();
    var copy = Path.Combine(this._directory, "iterators.mjs");
    File.WriteAllText(copy, SequenceFixtures.SourceOf("iterators.js"));
    return (calls, Toolchain.NodeOutcomes(this._directory, Path.GetFileName(copy), calls, true));
  }

  /// <summary>Test design 1 to 7: the JavaScript fixture in every C# profile computes what node computes of the original.</summary>
  [Test]
  public void JavaScriptFixture_RunsInEveryCSharpProfileLikeNode() {
    var (calls, expected) = this.Original();
    foreach (var profile in CSharpProfile.All) {
      var text = this.Translate(SequenceFixtures.PathOf("iterators.js"), "csharp", "iterators.cs", profile.Name, "--profile", profile.Name);
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
      Assert.That(calls.Select(GeneratedCSharp.Compile(text, false).Outcome), Is.EqualTo(expected), $"{profile.Name}\n{text}");
    }
  }

  /// <summary>Test design 1 to 7: the JavaScript fixture at every JavaScript level computes in node what the original computes.</summary>
  [Test]
  public void JavaScriptFixture_RunsInEveryJavaScriptProfileLikeNode() {
    var (calls, expected) = this.Original();
    foreach (var profile in ProfileCase.All) {
      var output = "js-" + Guid.NewGuid().ToString("N");
      var text = this.Translate(SequenceFixtures.PathOf("iterators.js"), "javascript", "iterators" + profile.Parsed.Extension, output, profile.Arguments);
      Assert.That(GeneratedJavaScript.Problems(text, profile.Parsed), Is.Empty, text);
      Assert.That(Toolchain.NodeOutcomes(Path.Combine(this._directory, output), "iterators" + profile.Parsed.Extension, calls, true, profile.Mode, profile.Global),
        Is.EqualTo(expected), $"{profile}\n{text}");
    }
  }

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
