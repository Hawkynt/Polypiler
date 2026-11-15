using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Backends.Testing;

namespace Polypiler.Tests.Enums;

/// <summary>
/// The enums written by the C# backend (#1318): the declarations and every operation in every profile
/// compile against the reference assemblies of the profile and compute what the interpreter computes;
/// the generic members of <c>System.Enum</c> from their first profile on, the ones that take a type before.
/// </summary>
[TestFixture]
public sealed class CSharpEnumEmissionTests {

  private static string Emitted(IrModule module, CSharpProfile profile) => LegalizedCSharp.Emit(module, profile)[0].Content;

  /// <summary>Test design 1, 2, 11 and 13: every operation over every underlying type in every profile is written, compiles and runs like the interpreter.</summary>
  [TestCaseSource(typeof(CSharpProfile), nameof(CSharpProfile.All))]
  public void EveryOperation_IsWrittenInEveryProfile_AndRunsLikeTheInterpreter(CSharpProfile profile) {
    var module = EnumFixtures.Module;
    Assert.That(LegalizedCSharp.Check(module, profile), Is.Empty);
    var text = Emitted(module, profile);
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
    var build = GeneratedCSharp.Compile(text, true);
    var mismatches = EnumFixtures.Calls()
      .Select(call => (call, Expected: PrototypeFixture.InterpretedOutcome(module, call), Actual: EnumFixtures.Normalized(build.Outcome(call))))
      .Where(result => result.Expected != result.Actual)
      .Select(result => $"{result.call}: {result.Actual}, the interpreter {result.Expected}")
      .ToList();
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} mismatches\n{text}");
  }

  /// <summary>Test design 11: the first profile of each generic member, and the one before it.</summary>
  [TestCase("net2.0", "global::System.Enum.IsDefined(typeof(Perm), ((Perm)v))")]
  [TestCase("net2.0", "((Perm)global::System.Enum.Parse(typeof(Perm), s, false))")]
  [TestCase("net2.0", "global::System.Enum.GetNames(typeof(S))")]
  [TestCase("net2.0", "((S[])global::System.Enum.GetValues(typeof(S)))")]
  [TestCase("netstandard2.0", "((Perm)global::System.Enum.Parse(typeof(Perm), s, true))")]
  [TestCase("netstandard2.1", "global::System.Enum.Parse<Perm>(s, true)")]
  [TestCase("netcoreapp3.1", "((Color[])global::System.Enum.GetValues(typeof(Color)))")]
  [TestCase("netcoreapp3.1", "global::System.Enum.IsDefined(typeof(S), ((S)v))")]
  [TestCase("net8.0", "global::System.Enum.GetValues<Color>()")]
  [TestCase("net8.0", "global::System.Enum.IsDefined<S>(((S)v))")]
  [TestCase("net8.0", "global::System.Enum.GetNames<Big>()")]
  [TestCase("net10.0", "(~((Perm)x) & ((Perm)f)) == 0")]
  [TestCase("net10.0", "(((Perm)x) & Perm.Exec) == Perm.Exec")]
  [TestCase("net2.0", "[global::System.Flags]\npublic enum Perm : int {\n  None = 0,\n  Read = 1,")]
  [TestCase("net2.0", "public enum Color : byte {\n  Red = 0,\n  Green = 5,\n  Blue = 6\n}")]
  [TestCase("net2.0", "internal enum Big : ulong {\n  Max = 18446744073709551615,")]
  [TestCase("net2.0", "High = -9223372036854775808")]
  [TestCase("net2.0", "public enum None : int { }")]
  [TestCase("net2.0", "case Color.Red:")]
  [TestCase("net2.0", "Color.Green")]
  public void Operation_HasTheFormOfTheProfile(string profile, string form)
    => Assert.That(Emitted(EnumFixtures.Module, CSharpProfile.Parse(profile)), Does.Contain(form));

  [Test]
  public void HasFlag_IsNeverWritten()
    => Assert.That(CSharpProfile.All.Select(profile => Emitted(EnumFixtures.Module, profile)), Has.None.Contains("HasFlag("));

  /// <summary>A constant of an enum that no member has is the number cast to the enum, a negative one in parentheses.</summary>
  [Test]
  public void ConstantWithoutAMember_IsTheCastNumber() {
    var signed = new IrEnumType("S", IrType.I32, [new("A", 0)]) { IsPublic = true };
    var local = new IrLocal("x", signed.Type);
    var function = new IrFunction("F", true, IrType.Bool, [], [local], new IrBlock([
      new IrDeclare(local, new IrConst(signed.Type, -5)),
      new IrReturn(new IrBinary(IrBinaryOp.Equal, new IrLocalRef(local), new IrConst(signed.Type, 7)))
    ]));
    var module = new IrModule("M", [function], [new IrModuleConst("K", signed.Type, [3]), new IrModuleConst("T", IrType.Array(signed.Type), [0, -1])]) { Types = [signed] };
    var text = Emitted(module, CSharpProfile.Net20);
    Assert.That(text, Does.Contain("S x = (S)(-5);"));
    Assert.That(text, Does.Contain("x == (S)7"));
    Assert.That(text, Does.Contain("private const S K = (S)3;"));
    Assert.That(text, Does.Contain("S.A, (S)(-1)"));
    Assert.That(GeneratedCSharp.ComplaintsFor(text, "net2.0", CSharpProfile.Net20.LanguageVersionName), Is.Empty, text);
  }

  /// <summary>A member whose name C# reserves gets an <c>@</c>; an enum a public function names is public even when the module does not export it.</summary>
  [Test]
  public void Keyword_AndExposedEnum_AreWritten() {
    var named = new IrEnumType("Mode", IrType.I32, [new("class", 0), new("default", 1)]);
    var function = new IrFunction("F", true, named.Type, [], [], new IrBlock([new IrReturn(new IrConst(named.Type, 1))]));
    var text = Emitted(new IrModule("M", [function]) { Types = [named] }, CSharpProfile.Net100);
    Assert.That(text, Does.Contain("public enum Mode : int {\n  @class = 0,\n  @default = 1\n}"));
    Assert.That(text, Does.Contain("=> Mode.@default;"));
    Assert.That(GeneratedCSharp.ComplaintsFor(text, "net10.0", CSharpProfile.Net100.LanguageVersionName), Is.Empty, text);
  }

}
