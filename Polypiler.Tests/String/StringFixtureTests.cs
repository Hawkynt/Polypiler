using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.String;

/// <summary>
/// The fixtures of the strings: each C# fixture imported, interpreted and written for every C#
/// profile against .NET running the original; each JavaScript fixture imported and written for every
/// JavaScript level. node runs the JavaScript in the acceptance tests.
/// </summary>
[TestFixture]
public sealed class StringFixtureTests {

  private static IrModule CSharpModule(string className) => PrototypeFixture.Import(className + ".cs", File.ReadAllText(StringFixtures.PathOf(className + ".cs")));

  private static IrModule JavaScriptModule(string file) {
    var result = JavaScriptFrontend.Import(file + ".js", File.ReadAllText(StringFixtures.PathOf(file + ".js")));
    Assert.That(result.Diagnostics.Where(diagnostic => diagnostic.Severity == Polypiler.Core.DiagnosticSeverity.Error), Is.Empty);
    return result.Module!;
  }

  private static string DotNet(string className, PrototypeCall call) => StringFixtures.Comparable(PrototypeFixture.InvokedOutcome(StringFixtures.Compiled(className, call.Method), call));

  /// <summary>Test design 1: the interpreter computes what .NET computes of every C# fixture.</summary>
  [TestCaseSource(typeof(StringFixtures), nameof(StringFixtures.CSharp))]
  public void CSharpFixture_IsInterpretedLikeDotNet(string className) {
    var module = CSharpModule(className);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    foreach (var (_, call) in StringFixtures.CSharpCalls().Where(entry => entry.Class == className))
      Assert.That(StringFixtures.Comparable(PrototypeFixture.InterpretedOutcome(module, call)), Is.EqualTo(DotNet(className, call)), call.ToString());
  }

  private static IEnumerable<TestCaseData> FixturesAndProfiles()
    => from className in StringFixtures.CSharp
       from profile in CSharpProfile.All
       select new TestCaseData(className, profile.Name).SetName($"{className} for {profile.Name}");

  /// <summary>Test design 1: every C# fixture compiles for every C# profile and computes what .NET computes.</summary>
  [TestCaseSource(nameof(FixturesAndProfiles))]
  public void CSharpFixture_CompilesForTheProfile_AndRunsLikeDotNet(string className, string profileName) {
    var profile = CSharpProfile.Parse(profileName);
    var text = CSharpEmitter.Emit(CSharpModule(className), profile)[0].Content;
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
    var build = GeneratedCSharp.Compile(text, true);
    foreach (var (_, call) in StringFixtures.CSharpCalls().Where(entry => entry.Class == className))
      Assert.That(StringFixtures.Comparable(build.Outcome(call)), Is.EqualTo(DotNet(className, call)), $"{call}\n{text}");
  }

  /// <summary>Test design 1 and 14: every fixture of both languages is written at every JavaScript level, as JavaScript of that level.</summary>
  [Test]
  public void EveryFixture_IsWrittenAtEveryLevel() {
    var modules = StringFixtures.CSharp.Select(CSharpModule).Concat(StringFixtures.JavaScript.Select(JavaScriptModule)).ToList();
    foreach (var level in JavaScriptProfile.LevelNames)
      foreach (var module in modules) {
        var profile = JavaScriptProfile.Parse(level);
        var text = JavaScriptEmitter.Emit(module, profile).Single().Content;
        Assert.That(GeneratedJavaScript.Problems(text, profile), Is.Empty, $"{module.Name} at {level}\n{text}");
      }
  }

  /// <summary>Test design 1: every JavaScript fixture compiles for every C# profile.</summary>
  [TestCaseSource(typeof(StringFixtures), nameof(StringFixtures.JavaScript))]
  public void JavaScriptFixture_CompilesForEveryProfile(string file) {
    var module = JavaScriptModule(file);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    foreach (var profile in CSharpProfile.All) {
      var text = CSharpEmitter.Emit(module, profile)[0].Content;
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, $"{profile.Name}\n{text}");
    }
  }

  /// <summary>The values of the issue, as node prints them: FNV-1a over the code units of a string.</summary>
  [TestCase("hello", 1335831723u)]
  [TestCase("h\u00E9llo\U0001F600", 4113148722u)]
  public void Hash_IsTheValueOfTheIssueInBothFixtures(string text, uint hash) {
    Assert.That(IrInterpreter.Call(CSharpModule("Fnv1aText"), "Hash", [text]), Is.EqualTo(hash));
    Assert.That(IrInterpreter.Call(JavaScriptModule("fnv1a-text"), "hash", [text]), Is.EqualTo(hash));
  }

}
