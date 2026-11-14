using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Objects;

/// <summary>
/// The classes of the C# fixtures written by the C# backend for every profile: the form of each
/// member the lowering table names, compiled against the reference assemblies of the profile at its
/// language version, and run on this runtime against what .NET computes of the original.
/// </summary>
[TestFixture]
internal sealed class CSharpClassEmissionTests {

  private static readonly bool[] _overflowChecking = [false, true];

  public static IEnumerable<TestCaseData> FixturesAndProfiles()
    => from fixture in ClassFixtures.CSharp
       from profile in CSharpProfile.All
       select new TestCaseData(fixture.File, fixture.Class, profile.Name).SetName($"{fixture.File} for {profile.Name}");

  private static string Emitted(string file, CSharpProfile profile) => CSharpEmitter.Emit(ClassFixtures.Imported(file), profile)[0].Content;

  private static string Emitted(string source, string profile) {
    var module = PrototypeFixture.Import("C.cs", source);
    var parsed = CSharpProfile.Parse(profile);
    Assert.That(CSharpEmitter.Check(module, parsed), Is.Empty);
    var text = CSharpEmitter.Emit(module, parsed)[0].Content;
    Assert.That(GeneratedCSharp.ComplaintsFor(text, parsed.Name, parsed.LanguageVersionName), Is.Empty, text);
    return text;
  }

  /// <summary>Test design 1: every fixture compiles for every profile and computes what .NET computes.</summary>
  [TestCaseSource(nameof(FixturesAndProfiles))]
  public void Fixture_CompilesForTheProfile_AndRunsLikeDotNet(string file, string className, string profileName) {
    var profile = CSharpProfile.Parse(profileName);
    var text = Emitted(file, profile);
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
    foreach (var checking in _overflowChecking) {
      var build = GeneratedCSharp.Compile(text, checking);
      foreach (var call in ClassFixtures.Calls().Where(call => call.File == file).Select(call => call.Call))
        Assert.That(build.Outcome(call), Is.EqualTo(PrototypeFixture.InvokedOutcome(ClassFixtures.Compiled(className, call.Method), call)), $"{call} with CheckOverflow {checking}\n{text}");
    }
  }

  /// <summary>Test design 9: a member reached through null throws NullReferenceException in every profile.</summary>
  [TestCaseSource(typeof(CSharpProfile), nameof(CSharpProfile.All))]
  public void NullReceiver_ThrowsInEveryProfile(CSharpProfile profile) {
    var build = GeneratedCSharp.Compile(Emitted("null", profile), false);
    foreach (var (method, arguments) in new (string, object[])[] { ("CallOnNull", []), ("ReadOnNull", []), ("WriteOnNull", [3]), ("PropertyOnNull", []) })
      Assert.That(build.Outcome(new PrototypeCall(method, arguments)), Is.EqualTo("trap: null reference"), method);
  }

  /// <summary>Test design 6: the form of each row of the lowering table at the language versions around it.</summary>
  [TestCase("net2.0", "Counter", "  public int Step {\n    get {\n      return this._Step;")]
  [TestCase("net2.0", "Counter", "  public int Count {\n    get {\n      return this._count;")]
  [TestCase("net3.5", "Counter", "  public int Step { get; set; }")]
  [TestCase("net4.6", "Counter", "  public int Count => this._count;")]
  [TestCase("net10.0", "Counter", "  public Counter() : this(0) {")]
  [TestCase("net10.0", "Counter", "  public static int Created = 0;")]
  [TestCase("net2.0", "Range", "public sealed class Range : global::System.IEquatable<Range> {")]
  [TestCase("netcoreapp3.1", "Range", "public sealed class Range : global::System.IEquatable<Range> {")]
  [TestCase("net8.0", "Range", "public sealed record Range(int Lo, int Hi) {")]
  public void LoweringTable_RowsAtTheirVersions(string profile, string file, string form)
    => Assert.That(Emitted(file, CSharpProfile.Parse(profile)), Does.Contain(form));

  /// <summary>Get-only and init-only auto-properties at the versions around C# 6 and C# 9.</summary>
  [TestCase("net3.5", "  public int Fixed { get; private set; }", "  public long Wide { get; set; }")]
  [TestCase("net4.6", "  public int Fixed { get; }", "  public long Wide { get; set; }")]
  [TestCase("net8.0", "  public int Fixed { get; }", "  public long Wide { get; set; }")]
  [TestCase("net2.0", "  private readonly int _Fixed;", "  private long _Wide;")]
  public void AutoProperties_FollowTheVersion(string profile, string fixedForm, string initForm) {
    var text = Emitted("""
      public sealed class Box {
        public Box(int value) { Fixed = value; }
        public int Fixed { get; }
        public long Wide { get; init; }
        public int Other { get; set; }
        public int Both { get => Fixed + Other; set => Other = value; }
      }
      public static class C { public static long F(int a) { var b = new Box(a) { Wide = 3 }; b.Both = 5; return b.Fixed + b.Wide + b.Both; } }
      """, profile);
    Assert.That(text, Does.Contain(fixedForm).And.Contain(initForm));
    Assert.That(GeneratedCSharp.Compile(text, false).Method("F").Invoke(null, [4]), Is.EqualTo(4L + 3 + 9));
  }

  /// <summary>An init-only auto-property only constructors write keeps its init accessor from C# 9 on.</summary>
  [TestCase("net4.8", "  public long Wide { get; set; }")]
  [TestCase("net8.0", "  public long Wide { get; init; }")]
  public void InitAccessor_WhereOnlyConstructorsWriteIt(string profile, string form)
    => Assert.That(Emitted("public sealed class Box { public Box(long w) { Wide = w; } public long Wide { get; init; } } public static class C { public static long F() => new Box(2).Wide; }", profile),
      Does.Contain(form));

  /// <summary>Test design 14: every node of the classes in every profile; nothing is unsupported or inexpressible.</summary>
  [TestCaseSource(typeof(CSharpProfile), nameof(CSharpProfile.All))]
  public void EveryClassNode_IsWrittenInEveryProfile(CSharpProfile profile) {
    var module = PrototypeFixture.Import("C.cs", """
      #nullable disable
      public struct P { public int X; }
      public sealed record R(int Lo, int Hi) { public int Extra; }
      public sealed class N {
        public static int Count;
        public R Range; public N Next; public P Point; private readonly int[] _cells;
        public N() { _cells = new int[2]; ++Count; }
        public int this0 => _cells.Length;
        public bool Same(N other) => ReferenceEquals(this, other) || other == null || Range == other.Range;
        public static N Make() => new N { Point = new P { X = 3 } };
      }
      public static class C {
        public static int F() {
          var n = N.Make();
          n.Next = new N();
          n.Range = new R(1, 2);
          n.Point.X += 1;
          var r = new R(1, 2) { Extra = 0 };
          return (n.Same(n.Next) ? 1 : 0) + n.Point.X + n.this0 + (n.Range == r ? 10 : 0) + N.Count;
        }
      }
      """);
    Assert.That(CSharpEmitter.Check(module, profile), Is.Empty);
    var text = CSharpEmitter.Emit(module, profile)[0].Content;
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
    var expected = IrInterpreter.Call(module, "F", []);
    foreach (var checking in _overflowChecking)
      Assert.That(GeneratedCSharp.Compile(text, checking).Method("F").Invoke(null, []), Is.EqualTo(expected), text);
  }

}
