using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Backends.Testing;

namespace Polypiler.Tests.String;

/// <summary>
/// The fixtures of the conversions between numbers and text (#1315): each C# fixture imported,
/// interpreted and written for every C# profile against .NET running the original; each JavaScript
/// fixture imported and written for every profile of both languages. node runs the JavaScript in the
/// acceptance tests.
/// </summary>
[TestFixture]
public sealed class NumberTextFixtureTests {

  private static IrModule CSharpModule(string file) => PrototypeFixture.Import(file + ".cs", File.ReadAllText(NumberTextFixtures.PathOf(file + ".cs")));

  private static IrModule JavaScriptModule(string file) {
    var result = JavaScriptFrontend.Import(file + ".js", File.ReadAllText(NumberTextFixtures.PathOf(file + ".js")));
    Assert.That(result.Diagnostics, Is.Empty);
    return result.Module!;
  }

  private static IEnumerable<PrototypeCall> Calls(string className) => NumberTextFixtures.CSharpCalls().Where(entry => entry.Class == className).Select(entry => entry.Call);

  private static IEnumerable<TestCaseData> CSharpFixtures() => NumberTextFixtures.CSharp.Select(fixture => new TestCaseData(fixture.Class, fixture.File).SetName($"{fixture.Class}"));

  /// <summary>Test design 1: the interpreter computes what .NET computes of every C# fixture.</summary>
  [TestCaseSource(nameof(CSharpFixtures))]
  public void CSharpFixture_IsInterpretedLikeDotNet(string className, string file) {
    var module = CSharpModule(file);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That(module.Caveats, Is.Empty);
    foreach (var call in Calls(className))
      Assert.That(PrototypeFixture.InterpretedOutcome(module, call), Is.EqualTo(PrototypeFixture.InvokedOutcome(NumberTextFixtures.Compiled(className, call.Method), call)), call.ToString());
  }

  private static IEnumerable<TestCaseData> FixturesAndProfiles()
    => from fixture in NumberTextFixtures.CSharp
       from profile in CSharpProfile.All
       select new TestCaseData(fixture.Class, fixture.File, profile.Name).SetName($"{fixture.Class} for {profile.Name}");

  /// <summary>Test design 1 and 13: every C# fixture compiles for every C# profile and computes what .NET computes.</summary>
  [TestCaseSource(nameof(FixturesAndProfiles))]
  public void CSharpFixture_CompilesForTheProfile_AndRunsLikeDotNet(string className, string file, string profileName) {
    var profile = CSharpProfile.Parse(profileName);
    var text = LegalizedCSharp.Emit(CSharpModule(file), profile)[0].Content;
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
    var build = GeneratedCSharp.Compile(text, true);
    foreach (var call in Calls(className))
      Assert.That(build.Outcome(call), Is.EqualTo(PrototypeFixture.InvokedOutcome(NumberTextFixtures.Compiled(className, call.Method), call)), $"{call}\n{text}");
  }

  /// <summary>Test design 1, 13 and 14: every fixture of both languages is written at every JavaScript level, as JavaScript of that level.</summary>
  [Test]
  public void EveryFixture_IsWrittenAtEveryLevel() {
    var modules = NumberTextFixtures.CSharp.Select(fixture => CSharpModule(fixture.File)).Concat(NumberTextFixtures.JavaScript.Select(JavaScriptModule)).ToList();
    foreach (var level in JavaScriptProfile.LevelNames)
      foreach (var module in modules) {
        var profile = JavaScriptProfile.Parse(level, "cjs");
        var text = LegalizedJavaScript.Emit(module, profile).Single().Content;
        Assert.That(GeneratedJavaScript.Problems(text, profile), Is.Empty, $"{module.Name} at {level}\n{text}");
      }
  }

  /// <summary>Test design 1: every JavaScript fixture compiles for every C# profile.</summary>
  [TestCaseSource(typeof(NumberTextFixtures), nameof(NumberTextFixtures.JavaScript))]
  public void JavaScriptFixture_CompilesForEveryProfile(string file) {
    var module = JavaScriptModule(file);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    foreach (var profile in CSharpProfile.All) {
      var text = LegalizedCSharp.Emit(module, profile)[0].Content;
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, $"{profile.Name}\n{text}");
    }
  }

}
