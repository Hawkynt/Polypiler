using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Records;

/// <summary>
/// The structs of the C# fixtures written by the C# backend for every profile: the form of each
/// declaration the lowering table names, compiled against the reference assemblies of the profile
/// at its language version, and run on this runtime against what .NET computes of the original.
/// </summary>
[TestFixture]
internal sealed class CSharpRecordEmissionTests {

  private static readonly bool[] _overflowChecking = [false, true];

  public static IEnumerable<TestCaseData> FixturesAndProfiles()
    => from fixture in RecordFixtures.CSharp
       from profile in CSharpProfile.All
       select new TestCaseData(fixture.File, fixture.Class, profile.Name).SetName($"{fixture.File} for {profile.Name}");

  private static string Emitted(string file, CSharpProfile profile) => CSharpEmitter.Emit(RecordFixtures.Imported(file), profile)[0].Content;

  /// <summary>Test design 1 and 9: every fixture compiles for every profile and computes what .NET computes.</summary>
  [TestCaseSource(nameof(FixturesAndProfiles))]
  public void Fixture_CompilesForTheProfile_AndRunsLikeDotNet(string file, string className, string profileName) {
    var profile = CSharpProfile.Parse(profileName);
    var text = Emitted(file, profile);
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
    foreach (var checking in _overflowChecking) {
      var build = GeneratedCSharp.Compile(text, checking);
      foreach (var call in RecordFixtures.Calls().Where(call => call.File == file).Select(call => call.Call))
        Assert.That(build.Outcome(call), Is.EqualTo(PrototypeFixture.InvokedOutcome(RecordFixtures.Compiled(className, call.Method), call)), $"{call} with CheckOverflow {checking}\n{text}");
    }
  }

  /// <summary>The declaration of a record struct follows the language version: written out before C# 10, a record struct from it on.</summary>
  [TestCase("net2.0", "public struct Money : global::System.IEquatable<Money> {")]
  [TestCase("netstandard2.0", "public struct Money : global::System.IEquatable<Money> {")]
  [TestCase("netcoreapp3.1", "public struct Money : global::System.IEquatable<Money> {")]
  [TestCase("net8.0", "public record struct Money(long Cents, int Currency);")]
  [TestCase("net10.0", "public record struct Money(long Cents, int Currency);")]
  public void RecordStruct_HasTheFormOfTheProfile(string profile, string declaration)
    => Assert.That(Emitted("Money", CSharpProfile.Parse(profile)), Does.Contain("\n" + declaration + "\n").Or.StartWith(declaration + "\n").Or.Contain("\n\n" + declaration));

  /// <summary>Test design 13: every node of the value records, written for every profile; nothing is unsupported or inexpressible.</summary>
  [TestCaseSource(typeof(CSharpProfile), nameof(CSharpProfile.All))]
  public void EveryRecordNode_IsWrittenInEveryProfile(CSharpProfile profile) {
    var module = RecordModules.Everything();
    Assert.That(CSharpEmitter.Check(module, profile), Is.Empty);
    var text = CSharpEmitter.Emit(module, profile)[0].Content;
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
    Assert.That(IrInterpreter.Call(module, "Run", [1]), Is.True);
    foreach (var checking in _overflowChecking)
      Assert.That(GeneratedCSharp.Compile(text, checking).Method("Run").Invoke(null, [1]), Is.True, text);
  }

  /// <summary>The hash of a struct of many fields: combined by hand before System.HashCode, by Combine up to eight fields, by a HashCode beyond.</summary>
  [TestCase("net4.8", 2, "hash = hash * -1521134295 + ")]
  [TestCase("netstandard2.1", 8, "return global::System.HashCode.Combine(this.F0, this.F1, this.F2, this.F3, this.F4, this.F5, this.F6, this.F7);")]
  [TestCase("netcoreapp3.1", 9, "global::System.HashCode hash = new global::System.HashCode();")]
  [TestCase("netcoreapp3.1", 16, "hash.Add(this.F15);")]
  [TestCase("net2.0", 0, "    return 0;")]
  [TestCase("netstandard2.1", 0, "    return 0;")]
  [TestCase("net10.0", 0, "public readonly record struct Wide();")]
  [TestCase("net10.0", 16, "public readonly record struct Wide(double F0, ")]
  public void HashOfAStruct_FollowsTheFramework(string profileName, int fields, string expected) {
    var profile = CSharpProfile.Parse(profileName);
    var text = CSharpEmitter.Emit(RecordModules.Wide(fields), profile)[0].Content;
    Assert.That(text, Does.Contain(expected));
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
    Assert.That(GeneratedCSharp.Compile(text, true).Method("Sum").Invoke(null, []), Is.EqualTo(fields * (fields - 1) / 2.0));
  }

  /// <summary>Test design 9: around each row of the lowering table the language version decides the form.</summary>
  [TestCase("net4.6", null, "public struct Frozen : global::System.IEquatable<Frozen> {", "new Frozen()")]
  [TestCase("net4.6", "7", "public struct Frozen : global::System.IEquatable<Frozen> {", "new Frozen()")]
  [TestCase("net4.6", "7.1", "public struct Frozen : global::System.IEquatable<Frozen> {", "Frozen f = default;")]
  [TestCase("net4.6", "7.2", "public readonly struct Frozen : global::System.IEquatable<Frozen> {", "Frozen f = default;")]
  [TestCase("net4.6", "9", "public struct Pair : global::System.IEquatable<Pair> {", "default(Pair).A")]
  [TestCase("net4.6", "10", "public record struct Pair(int A, Frozen B) {", "  public Frozen B = B;")]
  [TestCase("net4.8", "10", "public readonly struct Frozen : global::System.IEquatable<Frozen> {", "public record struct Pair(int A, Frozen B)")]
  [TestCase("net8.0", null, "public readonly record struct Frozen(long X);", "Frozen f = default;")]
  public void LanguageVersion_DecidesTheForm(string profileName, string? languageVersion, string declaration, string use) {
    const string Source = "public readonly struct Frozen { public readonly long X; } public record struct Pair(int A, Frozen B); "
      + "public static class C { public static long F() { Frozen f = new Frozen(); var p = new Pair(1, f); p.B = default; return p.A + f.X + G(); } "
      + "private static long G() => default(Pair).A; private static bool H(Pair a) => a == a; }";
    var module = PrototypeFixture.Import("C.cs", Source);
    var profile = CSharpProfile.Parse(profileName, languageVersion);
    var text = CSharpEmitter.Emit(module, profile)[0].Content;
    Assert.That(text, Does.Contain(declaration).And.Contain(use));
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
    Assert.That(GeneratedCSharp.Compile(text, true).Method("F").Invoke(null, []), Is.EqualTo(1L));
  }

  /// <summary>A struct, a field or a name C# reserves, or one a member already has, is written so that C# reads it as it is meant.</summary>
  [Test]
  public void Names_ThatCSharpCannotTakeAsTheyAre_AreChanged() {
    var keyword = new IrStructType("event", [new IrField("class", IrType.I32, false), new IrField("event", IrType.I32, false)]);
    var clash = new IrStructType("M", [new IrField("X", IrType.I32, false)]);
    var member = new IrStructType("F", [new IrField("Y", IrType.I32, false)]);
    var e = new IrLocal("e", IrType.Struct(keyword));
    var body = new IrBlock([
      new IrDeclare(e, new IrStructNew(IrType.Struct(keyword), [new IrConst(IrType.I32, 1), new IrConst(IrType.I32, 2)])),
      new IrReturn(new IrBinary(IrBinaryOp.Add, new IrBinary(IrBinaryOp.Add, new IrFieldLoad(new IrLocalRef(e), keyword.Fields[0]), new IrFieldLoad(new IrLocalRef(e), keyword.Fields[1])),
        new IrBinary(IrBinaryOp.Add, new IrFieldLoad(new IrStructDefault(IrType.Struct(clash)), clash.Fields[0]), new IrFieldLoad(new IrStructDefault(IrType.Struct(member)), member.Fields[0]))))
    ]);
    var module = new IrModule("M", [new IrFunction("F", true, IrType.I32, [], [e], body)]) { Types = [keyword, clash, member] };
    var text = CSharpEmitter.Emit(module, CSharpProfile.Net100)[0].Content;
    Assert.That(text, Does.Contain("public struct @event {").And.Contain("public int @class;").And.Contain("public int event_;").And.Contain("public struct M_ {").And.Contain("public struct F_ {"));
    Assert.That(GeneratedCSharp.Compile(text, false).Method("F").Invoke(null, []), Is.EqualTo(3));
  }

}
