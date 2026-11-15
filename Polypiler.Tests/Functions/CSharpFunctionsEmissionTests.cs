using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Backends.Testing;

namespace Polypiler.Tests.Functions;

/// <summary>
/// Function values written by the C# backend in every profile (#1326): anonymous methods and declared
/// delegate types for .NET Framework 2.0, Func and Action from 3.5, local functions from C# 7, static
/// lambdas from C# 9; each output compiled against the profile's reference assemblies at its language
/// version and run against .NET.
/// </summary>
[TestFixture]
internal sealed class CSharpFunctionsEmissionTests {

  public static IEnumerable<TestCaseData> FixturesAndProfiles()
    => from fixture in FunctionsFixtures.CSharp
       from profile in CSharpProfile.All
       select new TestCaseData(fixture.File, fixture.Class, profile.Name).SetName($"{fixture.File} for {profile.Name}");

  private static string Emitted(IrModule module, string profile) => LegalizedCSharp.Emit(module, CSharpProfile.Parse(profile))[0].Content;

  private static string Compiled(IrModule module, string profile) {
    var text = Emitted(module, profile);
    var parsed = CSharpProfile.Parse(profile);
    Assert.That(GeneratedCSharp.ComplaintsFor(text, parsed.Name, parsed.LanguageVersionName), Is.Empty, text);
    return text;
  }

  private static string Snippet(string methods, string profile)
    => Compiled(PrototypeFixture.Import("Snippet.cs", "#nullable disable\nusing System;\n" + PrototypeFixture.Wrap(methods)), profile);

  /// <summary>Test design 1 and 6: every fixture compiles for every profile and computes what .NET computes.</summary>
  [TestCaseSource(nameof(FixturesAndProfiles))]
  public void Fixture_CompilesForTheProfile_AndRunsLikeDotNet(string file, string className, string profile) {
    var text = Compiled(FunctionsFixtures.Imported(file), profile);
    var build = GeneratedCSharp.Compile(text, false);
    foreach (var call in FunctionsFixtures.Calls().Where(call => call.File == file).Select(call => call.Call))
      Assert.That(build.Outcome(call), Is.EqualTo(PrototypeFixture.InvokedOutcome(FunctionsFixtures.Compiled(className, call.Method), call)), $"{call}\n{text}");
  }

  /// <summary>Scenario "Each profile writes the form it has".</summary>
  [Test]
  public void EachProfile_WritesTheFormItHas() {
    var closures = FunctionsFixtures.Imported("Closures");
    var net20 = Emitted(closures, "net2.0");
    Assert.That(net20, Does.Contain("delegate (").And.Contain("delegate int Int32ToInt32(int a1);").And.Not.Contain("=>").And.Not.Contain("System.Func"));
    var net35 = Emitted(closures, "net3.5");
    Assert.That(net35, Does.Contain("System.Func<int, int>").And.Contain("=>"));
    Assert.That(net35, Does.Contain("global::System.Func<int, int> F = null;"), "a recursive lambda is assigned null first");
    Assert.That(net35, Does.Not.Contain("int F(int m)"), "no local function");
    var net48 = Emitted(closures, "net4.8");
    Assert.That(net48, Does.Contain("int F(int m) => ").And.Not.Contain("static int F("), "a local function, not static before C# 8");
    Assert.That(net48, Does.Not.Contain("static v =>"));
    var net10 = Emitted(closures, "net10.0");
    Assert.That(net10, Does.Contain("static int F(int m) => (m <= 1) ? 1 : (m * F(m - 1));"));
    Assert.That(net10, Does.Contain("Compose(static v => v + 1, static v => v * 3)(x)"));
    Assert.That(net10, Does.Contain("fs[k++] = () => i;"), "a lambda that captures is not static");
  }

  /// <summary>Test design 5: Func and Action by arity per profile, a declared delegate type where the framework has none.</summary>
  [TestCase("net2.0", 0, false, "delegate int ToInt32();")]
  [TestCase("net2.0", 1, true, "global::System.Action<int>")]
  [TestCase("net2.0", 0, true, "delegate void ToVoid();")]
  [TestCase("net3.5", 0, true, "global::System.Action ")]
  [TestCase("net3.5", 4, false, "global::System.Func<int, int, int, int, int>")]
  [TestCase("net3.5", 5, false, "delegate int Int32Int32Int32Int32Int32ToInt32(")]
  [TestCase("net3.5", 4, true, "global::System.Action<int, int, int, int>")]
  [TestCase("net3.5", 5, true, "delegate void Int32Int32Int32Int32Int32ToVoid(")]
  [TestCase("net4.6", 5, false, "global::System.Func<int, int, int, int, int, int>")]
  [TestCase("net4.6", 16, false, "global::System.Func<int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int>")]
  [TestCase("net4.6", 16, true, "global::System.Action<int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int>")]
  [TestCase("net10.0", 17, false, "ToInt32(int a1,")]
  [TestCase("net10.0", 1, false, "global::System.Func<int, int>")]
  public void FunctionType_IsWrittenAsTheProfileHasIt(string profile, int arity, bool action, string written) {
    var parameters = string.Join(", ", Enumerable.Range(1, arity).Select(index => $"int a{index}"));
    var arguments = string.Join(", ", Enumerable.Range(1, arity));
    var declaration = action ? $"public delegate void D({parameters});" : $"public delegate int D({parameters});";
    var body = action ? $"var s = 0; D d = null; d = ({string.Join(", ", Enumerable.Range(1, arity).Select(index => $"a{index}"))}) => {{ s = s + 1; }}; d({arguments}); return s;"
      : $"D d = null; d = ({string.Join(", ", Enumerable.Range(1, arity).Select(index => $"a{index}"))}) => {arity}; return d({arguments});";
    var module = PrototypeFixture.Import("Snippet.cs", $"#nullable disable\nusing System;\n{declaration}\n" + PrototypeFixture.Wrap($"public static int F() {{ {body} }}"));
    var text = Compiled(module, profile);
    Assert.That(text, Does.Contain(written), text);
    Assert.That(GeneratedCSharp.Compile(text, false).Outcome(new PrototypeCall("F", [])), Is.EqualTo(action ? "1" : arity.ToString(System.Globalization.CultureInfo.InvariantCulture)));
  }

  /// <summary>A delegate type a public function names is public; one only private code names is private.</summary>
  [Test]
  public void DelegateType_IsPublicOnlyWhereAPublicSignatureNamesIt() {
    var text = Snippet("public static Func<int, int> F() => v => v; static int G() { Func<int, bool> h = v => v > 0; return h(1) ? 1 : 0; }", "net2.0");
    Assert.That(text, Does.Contain("public delegate int Int32ToInt32(int a1);").And.Contain("private delegate bool Int32ToBoolean(int a1);"), text);
  }

  /// <summary>
  /// Test design 7: a loop of variables of every pass - from JavaScript's for-let - copies its variable
  /// into the body; a body that writes it copies it back in a finally, which continue and break run too.
  /// </summary>
  [TestCase(false, 3)]
  [TestCase(true, 9)]
  public void PerIterationLoop_CopiesItsVariable(bool assigned, int expected) {
    var i = new IrLocal("i", IrType.I32);
    var fs = new IrLocal("fs", IrType.Array(IrType.Function(IrType.I32)));
    var k = new IrLocal("k", IrType.I32);
    IrExpr I(long value) => new IrConst(IrType.I32, value);
    List<IrStmt> body = [new IrStore(new IrLocalRef(fs), new IrStep(new IrLocalRef(k), true, true, false), IrLambda.Of([], IrType.I32, [], new IrBlock([new IrReturn(new IrLocalRef(i))])))];
    if (assigned)
      body.Add(new IrExprStmt(new IrStep(new IrLocalRef(i), true, false, false)));

    var t = new IrLocal("t", IrType.I32);
    var loop = new IrFor(new IrBinary(IrBinaryOp.Less, new IrLocalRef(i), I(assigned ? 6 : 3)), new IrBlock(body),
      new IrBlock([new IrDeclare(t, new IrBinary(IrBinaryOp.Add, new IrLocalRef(i), I(1))), new IrAssign(i, new IrLocalRef(t))])) { PerIteration = [i] };
    IrExpr At(int index) => new IrInvoke(IrType.I32, new IrArrayLoad(new IrLocalRef(fs), I(index)), []);
    var function = new IrFunction("F", true, IrType.I32, [], [fs, k, i, t], new IrBlock([
      new IrDeclare(fs, new IrNewArray(fs.Type, I(3))), new IrDeclare(k, I(0)), new IrDeclare(i, I(0)), loop,
      new IrReturn(new IrBinary(IrBinaryOp.Add, new IrBinary(IrBinaryOp.Add, At(0), At(1)), At(2)))]));
    var module = new IrModule("M", [function]);
    Assert.That(IrInterpreter.Call(module, "F", []), Is.EqualTo(expected));
    foreach (var profile in new[] { "net2.0", "net3.5", "netstandard2.0", "net10.0" }) {
      var text = Compiled(module, profile);
      Assert.That(text, Does.Contain("int i_1 = i;"), text);
      Assert.That(text.Contains("finally", System.StringComparison.Ordinal), Is.EqualTo(assigned), text);
      Assert.That(GeneratedCSharp.Compile(text, false).Outcome(new PrototypeCall("F", [])), Is.EqualTo(expected.ToString(System.Globalization.CultureInfo.InvariantCulture)), text);
    }
  }

  /// <summary>A lambda called where it stands, a conditional of two lambdas and a comparison of a local function with null are cast to their type.</summary>
  [TestCase("net2.0")]
  [TestCase("net3.5")]
  [TestCase("net10.0")]
  public void UntypedPlaces_CastTheFunction(string profile) {
    var text = Snippet("""
      public static int F(bool c, int x) {
        Func<int, int> f = c ? v => v + 1 : v => v - 1;
        return f(x) + ((Func<int, int>)(v => v * 2))(x);
      }
      public static bool G() { int H() => 1; Func<int> h = H; return h != null && H() == 1; }
      """, profile);
    var build = GeneratedCSharp.Compile(text, false);
    Assert.That((build.Outcome(new PrototypeCall("F", [true, 3])), build.Outcome(new PrototypeCall("F", [false, 3])), build.Outcome(new PrototypeCall("G", []))), Is.EqualTo(("10", "8", "true")), text);
  }

  /// <summary>A generic method that takes a function and a lambda in a method of a class that captures this compile and run in every profile.</summary>
  [TestCaseSource(typeof(CSharpProfile), nameof(CSharpProfile.All))]
  public void GenericsAndReceivers_CompileInEveryProfile(CSharpProfile profile) {
    var module = PrototypeFixture.Import("Snippet.cs", """
      #nullable disable
      using System;
      public sealed class Counter {
        private int _count;
        public int Add(int times) { Action step = () => this._count++; for (var i = 0; i < times; i++) step(); return this._count; }
      }
      public static class Snippet {
        static T Apply<T>(Func<T, T> f, T v) => f(v);
        public static int F(int x) => Apply(v => v + 1, x) + new Counter().Add(3);
        public static double G(double x) => Apply(v => v * 2, x);
      }
      """);
    var text = Compiled(module, profile.Name);
    var build = GeneratedCSharp.Compile(text, false);
    Assert.That((build.Outcome(new PrototypeCall("F", [4])), build.Outcome(new PrototypeCall("G", [1.5]))), Is.EqualTo(("8", PrototypeFixture.Show(3.0))), text);
  }

  /// <summary>A static field of a function type without a value starts as <c>null</c> in every profile, and a lambda stored in it later runs.</summary>
  [TestCaseSource(typeof(CSharpProfile), nameof(CSharpProfile.All))]
  public void StaticFunctionField_WithoutAValue_StartsAsNull(CSharpProfile profile) {
    var text = Snippet("static Func<int, int> keep; static int H(Func<int, int> f) { int before = keep == null ? 10 : 20; keep = f; return before + f(1); } public static int R() => H(x => x + 1) + keep(5);", profile.Name);
    Assert.That(text, Does.Contain(" keep = null;"), text);
    Assert.That(GeneratedCSharp.Compile(text, false).Outcome(new PrototypeCall("R", [])), Is.EqualTo("18"), text);
  }

  /// <summary>Test design 13: no construct of function values is inexpressible in any profile.</summary>
  [Test]
  public void NoProfile_FindsAFunctionValueInexpressible() {
    foreach (var (file, _) in FunctionsFixtures.CSharp)
      foreach (var profile in CSharpProfile.All)
        Assert.That(LegalizedCSharp.Check(FunctionsFixtures.Imported(file), profile), Is.Empty, $"{file} for {profile.Name}");
  }

}
