using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The differential rows of the test design of <c>tests/features/ir/ir-collections.feature</c>, with
/// the real <c>polypiler</c> executable in process, <c>node</c> and the C# compiler: contents, order,
/// traps and the step at which a changed collection traps agree between .NET, node, the interpreter and
/// every profile of both backends. The expected outcome of a call is never computed here: it is what
/// .NET computes of the original C# fixture, built into this assembly, or what <c>node</c> computes of
/// the original JavaScript.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd")]
[Layer("core")]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase), Parallelizable(ParallelScope.All)]
public sealed class CollectionTests {

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

  private static IEnumerable<TestCaseData> LevelsAndFixtures()
    => from profile in ProfileCase.All
       from fixture in CollectionFixtures.CSharp
       select new TestCaseData(profile, fixture.File, fixture.Class).SetName($"{fixture.File} in {profile}");

  /// <summary>Test design 1, 4, 5, 6, 7 and 10: every call of every C# fixture computes in node at every level what .NET computes.</summary>
  [TestCaseSource(nameof(LevelsAndFixtures))]
  public void CSharpFixture_RunsInNodeLikeDotNet(ProfileCase profile, string file, string className) {
    var calls = CollectionFixtures.Calls().Where(call => call.File == file).ToList();
    var text = this.Translate(CollectionFixtures.PathOf(file + ".cs"), "javascript", className + profile.Parsed.Extension, "out", profile.Arguments);
    Assert.That(GeneratedJavaScript.Problems(text, profile.Parsed), Is.Empty, text);
    var outcomes = Toolchain.NodeOutcomes(Path.Combine(this._directory, "out"), className + profile.Parsed.Extension, [.. calls.Select(call => call.Call)], true, profile.Mode, profile.Global);
    for (var index = 0; index < calls.Count; ++index)
      Assert.That(outcomes[index], Is.EqualTo(PrototypeFixture.InvokedOutcome(CollectionFixtures.Compiled(className, calls[index].Call.Method), calls[index].Call)), $"{calls[index].Call} in {profile}\n{text}");
  }

  /// <summary>Test design 1 and 9: every call of every C# fixture computes in every C# profile what .NET computes.</summary>
  [TestCase("Collections", "CollectionsDemo"), TestCase("WordFrequency", "WordFrequencyDemo")]
  public void CSharpFixture_RunsInEveryCSharpProfileLikeDotNet(string file, string className) {
    var calls = CollectionFixtures.Calls().Where(call => call.File == file).Select(call => call.Call).ToList();
    var expected = calls.Select(call => PrototypeFixture.InvokedOutcome(CollectionFixtures.Compiled(className, call.Method), call)).ToList();
    foreach (var profile in CSharpProfile.All) {
      var text = this.Translate(CollectionFixtures.PathOf(file + ".cs"), "csharp", className + ".cs", profile.Name, "--profile", profile.Name);
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
      Assert.That(calls.Select(GeneratedCSharp.Compile(text, false).Outcome), Is.EqualTo(expected), $"{profile.Name}\n{text}");
    }
  }

  /// <summary>Test design 4, 5, 6 and 9: the module of every list operation computes in node at every level what the interpreter computes.</summary>
  [TestCaseSource(typeof(ProfileCase), nameof(ProfileCase.All))]
  public void ListModule_RunsInNodeLikeTheInterpreter(ProfileCase profile) => this.ModuleRunsInNode(profile, CollectionModules.Lists(), [.. CollectionModules.ListCalls()]);

  /// <summary>Test design 2, 3, 4, 9 and 10: the module of every map and set operation computes in node at every level what the interpreter computes.</summary>
  [TestCaseSource(typeof(ProfileCase), nameof(ProfileCase.All))]
  public void MapModule_RunsInNodeLikeTheInterpreter(ProfileCase profile) => this.ModuleRunsInNode(profile, CollectionModules.Maps(), [.. CollectionModules.MapCalls()]);

  private void ModuleRunsInNode(ProfileCase profile, Core.IR.IrModule module, List<PrototypeCall> calls) {
    var text = Core.Backends.JavaScript.JavaScriptEmitter.Emit(module, profile.Parsed).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(text, profile.Parsed), Is.Empty, text);
    File.WriteAllText(Path.Combine(this._directory, module.Name + profile.Parsed.Extension), text);
    var outcomes = Toolchain.NodeOutcomes(this._directory, module.Name + profile.Parsed.Extension, calls, true, profile.Mode, profile.Global);
    Assert.That(outcomes, Is.EqualTo(calls.Select(call => PrototypeFixture.InterpretedOutcome(module, call))), $"{profile}\n{text}");
  }

  /// <summary>The calls of the JavaScript fixture and what node computes of the original.</summary>
  private (List<PrototypeCall> Calls, IReadOnlyList<string> Expected) Original(string file) {
    var calls = (file == "collections.js" ? CollectionFixtures.ScriptCalls() : CollectionFixtures.HistogramCalls()).ToList();
    var copy = Path.Combine(this._directory, Path.ChangeExtension(file, ".mjs"));
    File.WriteAllText(copy, CollectionFixtures.SourceOf(file));
    return (calls, Toolchain.NodeOutcomes(this._directory, Path.GetFileName(copy), calls, true));
  }

  /// <summary>Test design 1, 4, 5 and 6: the JavaScript fixture in every C# profile computes what node computes of the original.</summary>
  [TestCase("collections.js"), TestCase("wordFrequency.js")]
  public void JavaScriptFixture_RunsInEveryCSharpProfileLikeNode(string file) {
    var (calls, expected) = this.Original(file);
    foreach (var profile in CSharpProfile.All) {
      var text = this.Translate(CollectionFixtures.PathOf(file), "csharp", Path.ChangeExtension(file, ".cs"), profile.Name, "--profile", profile.Name);
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
      Assert.That(calls.Select(GeneratedCSharp.Compile(text, false).Outcome), Is.EqualTo(expected), $"{profile.Name}\n{text}");
    }
  }

  /// <summary>Test design 1 and 4: the JavaScript fixture at every JavaScript level computes in node what the original computes.</summary>
  [TestCase("collections.js"), TestCase("wordFrequency.js")]
  public void JavaScriptFixture_RunsInEveryJavaScriptProfileLikeNode(string file) {
    var (calls, expected) = this.Original(file);
    var name = Path.GetFileNameWithoutExtension(file);
    foreach (var profile in ProfileCase.All) {
      var output = "js-" + Guid.NewGuid().ToString("N");
      var text = this.Translate(CollectionFixtures.PathOf(file), "javascript", name + profile.Parsed.Extension, output, profile.Arguments);
      Assert.That(GeneratedJavaScript.Problems(text, profile.Parsed), Is.Empty, text);
      Assert.That(Toolchain.NodeOutcomes(Path.Combine(this._directory, output), name + profile.Parsed.Extension, calls, true, profile.Mode, profile.Global),
        Is.EqualTo(expected), $"{profile}\n{text}");
    }
  }

}