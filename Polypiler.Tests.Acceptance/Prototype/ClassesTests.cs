using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The differential rows of the test design of <c>tests/features/ir/ir-classes.feature</c>, with
/// the real <c>polypiler</c> executable or in process, <c>node</c> and the C# compiler. The expected
/// outcome of a call is never computed here: it is what .NET computes of the original C# fixture,
/// built into this assembly, or what <c>node</c> computes of the original JavaScript.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd")]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase), Parallelizable(ParallelScope.All)]
public sealed class ClassesTests {

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
    return File.ReadAllText(Path.Combine(directory, generated));
  }

  private static string DotNet(string className, PrototypeCall call) => PrototypeFixture.InvokedOutcome(ClassFixtures.Compiled(className, call.Method), call);

  /// <summary>Test design 1: every call of every C# fixture in every JavaScript level and module format computes in node what .NET computes.</summary>
  [TestCaseSource(nameof(LevelsAndFixtures))]
  public void CSharpFixture_RunsInNodeLikeDotNet(ProfileCase profile, string file, string className) {
    var calls = ClassFixtures.Calls().Where(call => call.File == file).ToList();
    var text = this.Translate(ClassFixtures.PathOf(file + ".cs"), "javascript", className + profile.Parsed.Extension, "out", profile.Arguments);
    Assert.That(GeneratedJavaScript.Problems(text, profile.Parsed), Is.Empty, text);
    var outcomes = Toolchain.NodeOutcomes(Path.Combine(this._directory, "out"), className + profile.Parsed.Extension, [.. calls.Select(call => call.Call)], true, profile.Mode, profile.Global);
    for (var index = 0; index < calls.Count; ++index)
      Assert.That(outcomes[index], Is.EqualTo(DotNet(className, calls[index].Call)), $"{calls[index].Call} in {profile}\n{text}");
  }

  private static IEnumerable<TestCaseData> LevelsAndFixtures()
    => from profile in ProfileCase.All
       from fixture in ClassFixtures.CSharp
       select new TestCaseData(profile, fixture.File, fixture.Class).SetName($"{fixture.File} in {profile}");

  /// <summary>A static field is shared by every object of its class, in node at every level as in the interpreter.</summary>
  [TestCase("es5", "cjs"), TestCase("es2015", "esm"), TestCase("es2022", "esm")]
  public void StaticField_CountsTheObjectsMadeInNode(string level, string format) {
    var extension = format == "esm" ? ".mjs" : ".cjs";
    this.Translate(ClassFixtures.PathOf("Counter.cs"), "javascript", "CounterDemo" + extension, level, "--profile", level, "--module", format);
    Assert.That(ClassesSteps.Node(Path.Combine(this._directory, level, "CounterDemo" + extension), format == "esm", "MadeByBoth", "console.log(MadeByBoth());"), Is.EqualTo("3"));
  }

  /// <summary>Test design 9: a member reached through null throws a TypeError in node at ECMAScript 5 and 2022, where .NET throws NullReferenceException.</summary>
  [TestCase("es5", "cjs"), TestCase("es2022", "esm")]
  public void NullReceiver_ThrowsATypeErrorInNode(string level, string format) {
    var text = this.Translate(ClassFixtures.PathOf("null.cs"), "javascript", "NullDemo" + (format == "esm" ? ".mjs" : ".cjs"), level, "--profile", level, "--module", format);
    var file = Path.Combine(this._directory, level, "NullDemo" + (format == "esm" ? ".mjs" : ".cjs"));
    foreach (var method in new[] { "CallOnNull", "ReadOnNull", "WriteOnNull", "PropertyOnNull" }) {
      Assert.That(PrototypeFixture.InvokedOutcome(ClassFixtures.Compiled("NullDemo", method), new PrototypeCall(method, method == "WriteOnNull" ? [3] : [])), Is.EqualTo("trap: null reference"));
      Assert.That(ClassesSteps.Node(file, format == "esm", method, $"try {{ {method}(3); console.log('none'); }} catch (error) {{ console.log(error.constructor.name); }}"), Is.EqualTo("TypeError"), $"{method}\n{text}");
    }
  }

}
