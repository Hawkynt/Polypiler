using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Backends.Testing;

namespace Polypiler.Tests.Objects;

/// <summary>
/// The hierarchies of the C# fixtures written by the C# backend for every profile: the form of each
/// row of the lowering table, compiled against the reference assemblies of the profile at its
/// language version, and run on this runtime against what .NET computes of the original.
/// </summary>
[TestFixture]
internal sealed class CSharpDispatchEmissionTests {

  public static IEnumerable<TestCaseData> FixturesAndProfiles()
    => from fixture in DispatchFixtures.CSharp
       from profile in CSharpProfile.All
       select new TestCaseData(fixture.File, fixture.Class, profile.Name).SetName($"{fixture.File} for {profile.Name}");

  private static string Emitted(string file, CSharpProfile profile) => LegalizedCSharp.Emit(DispatchFixtures.Imported(file), profile)[0].Content;

  /// <summary>Test design 1: every fixture compiles for every profile and computes what .NET computes, the invalid cast included.</summary>
  [TestCaseSource(nameof(FixturesAndProfiles))]
  public void Fixture_CompilesForTheProfile_AndRunsLikeDotNet(string file, string className, string profileName) {
    var profile = CSharpProfile.Parse(profileName);
    var text = Emitted(file, profile);
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
    var build = GeneratedCSharp.Compile(text, false);
    foreach (var call in DispatchFixtures.Calls().Where(call => call.File == file).Select(call => call.Call))
      Assert.That(build.Outcome(call), Is.EqualTo(PrototypeFixture.InvokedOutcome(DispatchFixtures.Compiled(className, call.Method), call)), $"{call}\n{text}");
  }

  /// <summary>Test design 3 and 7: the form of each row of the lowering table, at the language versions around C# 7.</summary>
  [TestCase("net2.0", "Shapes", "public interface IShape {\n\n  double Area();")]
  [TestCase("net2.0", "Shapes", "public abstract class Shape : IShape {")]
  [TestCase("net2.0", "Shapes", "  public abstract double Area();")]
  [TestCase("net2.0", "Shapes", "  public virtual int Sides()")]
  [TestCase("net2.0", "Shapes", "public sealed class Rect : Shape {")]
  [TestCase("net2.0", "Shapes", "  public override double Area()")]
  [TestCase("net2.0", "Shapes", "return o is Rect;")]
  [TestCase("net2.0", "Shapes", "(o as Rect) == null")]
  [TestCase("net2.0", "Shapes", "((Rect)o).Area()")]
  [TestCase("net2.0", "Shapes", "if (o is Rect) {\n      Rect rect = (Rect)o;")]
  [TestCase("net4.6", "Shapes", "if (o is Rect) {\n      var rect = (Rect)o;")]
  [TestCase("net2.0", "Shapes", "public Rect(double width, double height) {")]
  [TestCase("net4.8", "Shapes", "if (o is Rect rect) {")]
  [TestCase("net10.0", "Shapes", "if (o is Rect rect) {")]
  [TestCase("net2.0", "Animals", "public class Animal {")]
  [TestCase("net2.0", "Animals", "return base.Base() + 1;")]
  [TestCase("net10.0", "Animals", "public sealed class Dog : Animal {")]
  public void LoweringTable_RowsAtTheirVersions(string profile, string file, string form)
    => Assert.That(Emitted(file, CSharpProfile.Parse(profile)), Does.Contain(form));

}
