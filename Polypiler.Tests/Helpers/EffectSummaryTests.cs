using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize.Passes;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Helpers;

/// <summary>
/// The effect summaries of functions (#1361), test design 2: one function per class of effect - pure,
/// reading a capture, a global, a field and an array, writing a capture, a global, a field, trapping,
/// calling a function that writes, recursive, calling through a function value - and the loops with and
/// without a bound the analysis proves. Where the analysis cannot know, the summary is the worst case.
/// </summary>
[TestFixture]
internal sealed class EffectSummaryTests {

  private const string Usings = "using System; ";

  private static IrModule Legalized(string methods) {
    var imported = CSharpFrontend.Import("Snippet.cs", Usings + PrototypeFixture.Wrap(methods));
    Assert.That(imported.Module, Is.Not.Null, string.Join(Environment.NewLine, imported.Diagnostics));
    return CSharpEmitter.Legalize(imported.Module!, () => PrecomputedImplementations.Module, CSharpProfile.Default, FloatMode.Accurate);
  }

  private static IrEffects Of(string methods, string function) => IrEffects.Of(Legalized(methods), function);

  private static IrPlace Global(string name) => new(IrPlaceKind.Global, name);

  private static IrPlace Elements(string type) => new(IrPlaceKind.Elements, type);

  private static IEnumerable<TestCaseData> Classes() {
    yield return new TestCaseData("static int F(int x) => x * x + 1;", IrEffectKinds.None, "pure").SetName("Effects_Pure");
    yield return new TestCaseData("static int g = 3; static int F(int x) => x + g;", IrEffectKinds.Reads, "reads reading g").SetName("Effects_ReadsAGlobal");
    yield return new TestCaseData("static int F(int[] a) => a.Length > 0 ? a[0] : 0;", IrEffectKinds.Reads | IrEffectKinds.Traps, "reads, traps reading i32[]").SetName("Effects_ReadsAnArrayElement");
    yield return new TestCaseData("static int g; static int F(int x) { g = x; return x; }", IrEffectKinds.Writes, "writes writing g").SetName("Effects_WritesAGlobal");
    yield return new TestCaseData("static int F(int[] a) { a[0] = 1; return 0; }", IrEffectKinds.Writes | IrEffectKinds.Traps, "writes, traps writing i32[]").SetName("Effects_WritesAnArrayElement");
    yield return new TestCaseData("static int F(int x) => 10 / x;", IrEffectKinds.Traps, "pure, traps").SetName("Effects_Traps");
    yield return new TestCaseData("static int F(int x) => checked(x + 1);", IrEffectKinds.Traps, "pure, traps").SetName("Effects_TrapsOnOverflow");
    yield return new TestCaseData("static int F(int x) => x < 0 ? throw new ArgumentException() : x;", IrEffectKinds.Traps, "pure, traps").SetName("Effects_ThrowExpression");
    yield return new TestCaseData("static int F(int x) { if (x < 0) throw new ArgumentException(); return x; }", IrEffectKinds.Traps, "pure, traps").SetName("Effects_Throws");
    yield return new TestCaseData("static int g; static void Set(int v) { g = v; } static int F(int x) { Set(x); return 1; }", IrEffectKinds.Writes, "writes writing g").SetName("Effects_CallsAFunctionThatWrites");
    yield return new TestCaseData("static int F(int n) => n < 2 ? n : F(n - 1) + F(n - 2);", IrEffectKinds.MayNotTerminate, "pure, may not end").SetName("Effects_Recursive");
    yield return new TestCaseData("static bool Odd(int n) => n != 0 && F(n - 1); static bool F(int n) => n == 0 || Odd(n - 1);", IrEffectKinds.MayNotTerminate, "pure, may not end").SetName("Effects_MutuallyRecursive");
    yield return new TestCaseData("static int F(int[] a) { int s = 0; for (int i = 0; i < a.Length; i++) s += a[i]; return s; }", IrEffectKinds.Reads | IrEffectKinds.Traps, "reads, traps reading i32[]").SetName("Effects_CountedLoopEnds");
    yield return new TestCaseData("static int F(int[] a) { int s = 0; foreach (var v in a) s += v; return s; }", IrEffectKinds.Reads | IrEffectKinds.Traps, "reads, traps reading i32[]").SetName("Effects_ForEachOverAnArrayEnds");
    yield return new TestCaseData("static int F(int n) { int s = 0; for (int i = n; i > 0; i--) s += i; return s; }", IrEffectKinds.None, "pure").SetName("Effects_CountdownEnds");
    yield return new TestCaseData("static int F(int n) { int s = 0; for (int i = 0; i <= n; i++) s += i; return s; }", IrEffectKinds.MayNotTerminate, "pure, may not end").SetName("Effects_InclusiveBoundMayNotEnd");
    yield return new TestCaseData("static int F(int n) { int s = 0; for (int i = 0; i < n; i += 2) s += i; return s; }", IrEffectKinds.MayNotTerminate, "pure, may not end").SetName("Effects_StepOfTwoMayNotEnd");
    yield return new TestCaseData("static int F(int x) { while (x != 1) x = (x & 1) == 0 ? x >> 1 : 3 * x + 1; return x; }", IrEffectKinds.MayNotTerminate, "pure, may not end").SetName("Effects_UnboundedLoopMayNotEnd");
    yield return new TestCaseData("static int F(int n) { int s = 0; int i = 0; while (i < n) { if (s > 3) continue; s++; i++; } return s; }", IrEffectKinds.MayNotTerminate, "pure, may not end").SetName("Effects_LoopThatContinuesPastItsStepMayNotEnd");
    yield return new TestCaseData("static int F(int n) { int s = 0; for (int i = 0; i < n; i++) { s += i; n--; } return s; }", IrEffectKinds.MayNotTerminate, "pure, may not end").SetName("Effects_LoopThatMovesItsBoundMayNotEnd");
    yield return new TestCaseData("static int F(int n) { int[] a = new int[n]; return a.Length; }", IrEffectKinds.Allocates | IrEffectKinds.Traps, "allocates, traps").SetName("Effects_Allocates");
    yield return new TestCaseData("static int F(Func<int, int> f) => f(1);", (IrEffectKinds)31, "writes, allocates, traps, may not end reading anything writing anything").SetName("Effects_IndirectCallIsTheWorstCase");
  }

  /// <summary>Test design 2: the summary of each class, and its words.</summary>
  [TestCaseSource(nameof(Classes))]
  public void Summary(string methods, IrEffectKinds kinds, string text) {
    var effects = Of(methods, "F");
    Assert.That(effects.Kinds, Is.EqualTo(kinds));
    Assert.That(effects.ToString(), Is.EqualTo(text));
  }

  /// <summary>A field of an object is read and written as that field of any object, and either may trap on null.</summary>
  [Test]
  public void ObjectField_IsAPlaceOfItsOwn() {
    const string source = """
      public sealed class Box { public int Value; }
      public static class Snippet {
        static int Get(Box b) => b.Value;
        static void Put(Box b) { b.Value = 2; }
        public static int F() { var b = new Box(); Put(b); return Get(b); }
      }
      """;
    var imported = CSharpFrontend.Import("Box.cs", source);
    Assert.That(imported.Module, Is.Not.Null, string.Join(Environment.NewLine, imported.Diagnostics));
    var table = IrEffects.Of(imported.Module!);
    Assert.That(table.Function("Get").Reads, Is.EquivalentTo(new[] { new IrPlace(IrPlaceKind.Field, "Value") }));
    Assert.That(table.Function("Put").Writes, Is.EquivalentTo(new[] { new IrPlace(IrPlaceKind.Field, "Value") }));
    Assert.That(table.Function("Get").MayTrap && table.Function("Put").MayTrap, Is.True);
    Assert.That(table.Function("F").ToString(), Is.EqualTo("writes, allocates, traps reading Value writing Value"), "the constructor runs");
  }

  /// <summary>A closure: what it reads and writes of what it captures is storage of its scope; the call of a held closure is the worst case.</summary>
  [Test]
  public void Closures_ReadAndWriteTheirCaptures() {
    var module = Legalized("static int F(int k) { int n = 0; Func<int> lim = () => k * 2; Func<int> inc = () => ++n; return lim() + inc(); }");
    var table = IrEffects.Of(module);
    var lambdas = IrTree.Nodes(module.Functions.Single(function => function.Name == "F").Body).OfType<IrLambda>().ToList();
    Assert.That(table.Lambda(lambdas[0]).ToString(), Is.EqualTo("reads reading k"));
    Assert.That(table.Lambda(lambdas[1]).ToString(), Is.EqualTo("writes reading n writing n"));
    Assert.That(table.Function("F").Kinds, Is.EqualTo((IrEffectKinds)31));
  }

  /// <summary>A name the module has no function of, an iterator and a struct field are the cases where the analysis knows nothing, the worst case and nothing.</summary>
  [Test]
  public void Unknown_IteratorAndStruct() {
    var module = Legalized("static System.Collections.Generic.IEnumerable<int> It(int n) { yield return n; } public struct P { public int X; } static int F(int x) { P p = new P(); p.X = x; return p.X; }");
    var table = IrEffects.Of(module);
    Assert.That(table.Function("NoSuchFunction"), Is.SameAs(IrEffects.Unknown));
    Assert.That(table.Function("It").Kinds, Is.EqualTo((IrEffectKinds)31));
    Assert.That(table.Function("F"), Is.Not.Null.And.Property(nameof(IrEffects.IsPure)).True, "a field of a struct in a variable is that variable");
    Assert.That(IrEffects.Of(module), Is.SameAs(table), "kept per list of functions");
    Assert.That(IrEffects.Of(module with { Functions = [.. module.Functions] }), Is.Not.SameAs(table), "a new list is analysed again");
  }

  /// <summary>Whether a write meets a read: of the same place, of anything, and of nothing.</summary>
  [Test]
  public void WritesWhatIsRead() {
    var readsG = IrEffects.None with { Reads = new HashSet<IrPlace> { Global("g") } };
    var writesG = IrEffects.None with { Writes = new HashSet<IrPlace> { Global("g") } };
    var writesH = IrEffects.None with { Writes = new HashSet<IrPlace> { Global("h") } };
    Assert.That(writesG.WritesWhatIsRead(readsG), Is.True);
    Assert.That(writesH.WritesWhatIsRead(readsG), Is.False);
    Assert.That(IrEffects.Unknown.WritesWhatIsRead(readsG), Is.True);
    Assert.That(writesH.WritesWhatIsRead(IrEffects.Unknown), Is.True);
    Assert.That(IrEffects.Unknown.WritesWhatIsRead(IrEffects.None), Is.False, "nothing read, nothing met");
    Assert.That(IrEffects.None.WritesWhatIsRead(readsG), Is.False);
    Assert.That(writesG.With(readsG).ToString(), Is.EqualTo("writes reading g writing g"));
    Assert.That(writesG.With(IrEffects.None).Same(writesG) && writesG.With(writesG).Same(writesG), Is.True);
    Assert.That(Elements("i32").ToString(), Is.EqualTo("i32[]"));
    Assert.That(IrEffects.Describe(IrEffectKinds.Allocates | IrEffectKinds.Reads), Is.EqualTo("reads, allocates"));
  }

  /// <summary>The purity of an expression with calls: a call of a pure function that ends and cannot trap is as pure as arithmetic; any other call is not.</summary>
  [Test]
  public void IsPure_OfCalls() {
    var module = Legalized("static int Sq(int x) => x * x; static int Div(int x) => 10 / x; static int Fib(int n) => n < 2 ? n : Fib(n - 1) + Fib(n - 2); static int g; static int G() => g;");
    var table = IrEffects.Of(module);
    IrExpr Call(string name, params long[] arguments) => new IrCall(name, IrType.I32, [.. arguments.Select(value => (IrExpr)new IrConst(IrType.I32, value))]);
    Assert.That(table.IsPure(new IrBinary(IrBinaryOp.Add, Call("Sq", 2), new IrConst(IrType.I32, 1))), Is.True);
    Assert.That(table.IsPure(Call("Div", 2)), Is.False, "may trap");
    Assert.That(table.IsPure(Call("Fib", 2)), Is.False, "may not end");
    Assert.That(table.IsPure(Call("G")), Is.False, "reads");
    Assert.That(table.IsPure(new IrConst(IrType.I32, 1)), Is.True);
    Assert.That(table.Expression(new IrBind(new IrLocal("t", IrType.I32), Call("G"))).ToString(), Is.EqualTo("writes reading g writing t"));
  }

}
