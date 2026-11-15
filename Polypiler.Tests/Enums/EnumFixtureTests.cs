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

namespace Polypiler.Tests.Enums;

/// <summary>
/// The fixtures of the enums (#1318): each C# fixture imported, interpreted and written for every C#
/// profile against .NET running the original; every fixture of both languages written at every
/// JavaScript level. node runs the JavaScript in the acceptance tests.
/// </summary>
[TestFixture]
public sealed class EnumFixtureTests {

  private static IrModule CSharpModule(string file) => PrototypeFixture.Import(file + ".cs", File.ReadAllText(EnumFixtures.PathOf(file + ".cs")));

  private static IrModule JavaScriptModule() {
    var result = JavaScriptFrontend.Import(EnumFixtures.JavaScript + ".js", File.ReadAllText(EnumFixtures.PathOf(EnumFixtures.JavaScript + ".js")));
    Assert.That(result.Diagnostics, Is.Empty);
    return result.Module!;
  }

  private static IEnumerable<PrototypeCall> Calls(string className) => EnumFixtures.CSharpCalls().Where(entry => entry.Class == className).Select(entry => entry.Call);

  private static string DotNet(string className, PrototypeCall call) => EnumFixtures.Normalized(PrototypeFixture.InvokedOutcome(EnumFixtures.Compiled(className, call.Method), call));

  private static IEnumerable<TestCaseData> CSharpFixtures() => EnumFixtures.CSharp.Select(fixture => new TestCaseData(fixture.Class, fixture.File).SetName(fixture.Class));

  /// <summary>Test design 1, 3 and 4: the interpreter computes what .NET computes of every C# fixture.</summary>
  [TestCaseSource(nameof(CSharpFixtures))]
  public void CSharpFixture_IsInterpretedLikeDotNet(string className, string file) {
    var module = CSharpModule(file);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    foreach (var call in Calls(className))
      Assert.That(PrototypeFixture.InterpretedOutcome(module, call), Is.EqualTo(DotNet(className, call)), call.ToString());
  }

  private static IEnumerable<TestCaseData> FixturesAndProfiles()
    => from fixture in EnumFixtures.CSharp
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
      Assert.That(EnumFixtures.Normalized(build.Outcome(call)), Is.EqualTo(DotNet(className, call)), $"{call}\n{text}");
  }

  /// <summary>Test design 1 and 13: every fixture of both languages is written at every JavaScript level, as JavaScript of that level.</summary>
  [Test]
  public void EveryFixture_IsWrittenAtEveryLevel() {
    var modules = EnumFixtures.CSharp.Select(fixture => CSharpModule(fixture.File)).Append(JavaScriptModule()).ToList();
    foreach (var level in JavaScriptProfile.LevelNames)
      foreach (var module in modules) {
        var profile = JavaScriptProfile.Parse(level, "cjs");
        Assert.That(LegalizedJavaScript.Check(module, profile), Is.Empty, $"{module.Name} at {level}");
        var text = LegalizedJavaScript.Emit(module, profile).Single().Content;
        Assert.That(GeneratedJavaScript.Problems(text, profile), Is.Empty, $"{module.Name} at {level}\n{text}");
      }
  }

  /// <summary>Test design 12: the JavaScript fixture's enum object is a public enum, and a parameter only members reach is of it.</summary>
  [Test]
  public void JavaScriptFixture_HasItsEnum() {
    var module = JavaScriptModule();
    var color = module.Types.OfType<IrEnumType>().Single();
    Assert.That((color.Name, color.IsPublic), Is.EqualTo(("Color", true)));
    Assert.That(module.Functions.Single(function => function.Name == "weight").Parameters[0].Type.EnumType, Is.SameAs(color));
    Assert.That(module.Functions.Single(function => function.Name == "shade").Parameters[0].Type, Is.EqualTo(IrType.I32));
    foreach (var profile in CSharpProfile.All) {
      var text = LegalizedCSharp.Emit(module, profile)[0].Content;
      Assert.That(text, Does.Contain("public enum Color : int").And.Contain("private static int weight(Color c)").And.Contain("a | (int)Color.Green"));
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
    }
  }

}
