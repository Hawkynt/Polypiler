using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Core.Optimize.Passes;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Helpers.Support;

namespace Polypiler.Tests.Helpers;

/// <summary>
/// The passes that work from the effect summaries (#1361): compile-time evaluation of pure calls (O0002)
/// within its budget, merging of identical calls (O0464) up to the writes between them, and dead-code
/// removal (O0005) of calls whose value nothing uses; the rows of test design 3 to 7 and the scenarios
/// of the issue. Every snippet is compiled by Roslyn as well, and its outcome there is the oracle.
/// </summary>
[TestFixture]
internal sealed class EffectPassTests {

  private const string Usings = "using System; ";

  private static string Source(string methods) => Usings + PrototypeFixture.Wrap(methods);

  private static IrModule Legalized(string methods) {
    var imported = CSharpFrontend.Import("Snippet.cs", Source(methods));
    Assert.That(imported.Module, Is.Not.Null, string.Join(Environment.NewLine, imported.Diagnostics));
    return CSharpEmitter.Legalize(imported.Module!, () => PrecomputedImplementations.Module, CSharpProfile.Default, FloatMode.Accurate);
  }

  private static OptimizationResult Optimized(IrModule module, params string[] disabled)
    => IrPassPipeline.Default.Run(module, new OptimizerOptions { Verify = true, Disabled = disabled.ToHashSet() });

  private static IrFunction Function(IrModule module, string name) => module.Functions.Single(function => function.Name == name);

  private static int Calls(IrModule module, string function, string callee)
    => IrTree.Nodes(Function(module, function).Body).OfType<IrCall>().Count(call => call.Function == callee);

  private static IReadOnlyList<string> Ids(OptimizationResult result) => [.. result.Applications.Select(application => application.Id)];

  /// <summary>The interpreter computes on both modules what .NET computes of the source, call by call, traps included.</summary>
  private static void SameOutcomes(string methods, IrModule before, IrModule after, params PrototypeCall[] calls) {
    var compiled = GeneratedCSharp.CompileSource(Source(methods));
    foreach (var call in calls) {
      var expected = compiled.Outcome(call);
      Assert.That(PrototypeFixture.InterpretedOutcome(before, call), Is.EqualTo(expected), $"before, {call}");
      Assert.That(PrototypeFixture.InterpretedOutcome(after, call), Is.EqualTo(expected), $"after, {call}");
    }
  }

  /// <summary>The interpreter computes on the module after a pass what it computes on the one before: for sources whose static state .NET keeps from call to call.</summary>
  private static void SameAsBefore(IrModule before, IrModule after, params PrototypeCall[] calls) {
    foreach (var call in calls)
      Assert.That(PrototypeFixture.InterpretedOutcome(after, call), Is.EqualTo(PrototypeFixture.InterpretedOutcome(before, call)), call.ToString());
  }

  // ---------------------------------------------------------------- the interpreter's budget

  private static IrModule Down() {
    var n = new IrLocal("n", IrType.I32);
    var down = new IrFunction("Down", true, IrType.I32, [n], [], new IrBlock([
      new IrReturn(new IrConditional(new IrBinary(IrBinaryOp.LessOrEqual, new IrLocalRef(n), new IrConst(IrType.I32, 0)), new IrConst(IrType.I32, 0),
        new IrCall("Down", IrType.I32, [new IrBinary(IrBinaryOp.Subtract, new IrLocalRef(n), new IrConst(IrType.I32, 1))])))]));
    return new IrModule("M", [down]);
  }

  /// <summary>A step is a statement executed or a call: <c>Down(n)</c> takes two per level, and a budget one short of that throws.</summary>
  [TestCase(0, 2)]
  [TestCase(3, 8)]
  public void Budget_CountsStatementsAndCalls(int argument, int steps) {
    Assert.That(IrInterpreter.Call(Down(), "Down", [argument], steps), Is.EqualTo(0));
    Assert.Throws<IrBudgetException>(() => IrInterpreter.Call(Down(), "Down", [argument], steps - 1));
  }

  /// <summary>Calls nested deeper than the interpreter allows within a budget stop it, however large the budget.</summary>
  [Test]
  public void Budget_BoundsTheDepth() {
    Assert.That(IrInterpreter.Call(Down(), "Down", [IrInterpreter.BudgetDepth - 1], long.MaxValue - 1), Is.EqualTo(0));
    Assert.That(Assert.Throws<IrBudgetException>(() => IrInterpreter.Call(Down(), "Down", [IrInterpreter.BudgetDepth], long.MaxValue - 1))!.Message, Does.Contain("deeper"));
  }

  /// <summary>A module with globals is refused: a call within a budget initialises none.</summary>
  [Test]
  public void Budget_RefusesAModuleWithGlobals() {
    var module = Down() with { Globals = [new IrGlobal("g", IrType.I32, null)] };
    Assert.Throws<ArgumentException>(() => IrInterpreter.Call(module, "Down", [1], 10));
  }

  /// <summary>A catch of the program does not catch the end of the budget.</summary>
  [Test]
  public void Budget_IsNoExceptionOfTheProgram() {
    var methods = "public static int Spin() { int n = 0; try { while (true) n++; } catch (Exception) { return -1; } }";
    var module = Legalized(methods);
    Assert.Throws<IrBudgetException>(() => IrInterpreter.Call(module, "Spin", [], 1000));
  }

  // ---------------------------------------------------------------- O0002 compile-time evaluation (rows 3 and 4)

  private const string Fib = "static int Fib(int n) => n < 2 ? n : Fib(n - 1) + Fib(n - 2); ";

  /// <summary>Scenario "A pure call with constant arguments is folded": <c>Fib(10)</c> is 55; <c>Fib(40)</c>, over the budget, stays a call with a note.</summary>
  [Test]
  public void Evaluation_FoldsAPureCall_AndKeepsOneOverTheBudget() {
    const string methods = Fib + "public static int G() => Fib(10); public static int G40() => Fib(40);";
    var module = Legalized(methods);
    var result = Optimized(module);
    Assert.That(Function(result.Module, "G").Body.Statements, Is.EqualTo(new IrStmt[] { new IrReturn(new IrConst(IrType.I32, 55)) }));
    Assert.That(Calls(result.Module, "G40", "Fib"), Is.EqualTo(1));
    Assert.That(result.Notes, Has.One.EqualTo($"O0002 compile-time evaluation: 'G40' keeps its call of 'Fib': its evaluation needs more than {O0002CallEvaluation.StepBudget} steps or nests calls deeper than {IrInterpreter.BudgetDepth}"));
    SameOutcomes(methods, module, result.Module, new PrototypeCall("G", []));
  }

  /// <summary>Test design 3: with a budget one short of what the call needs it stays; with exactly that and one more it is folded.</summary>
  [TestCase(-1, false)]
  [TestCase(0, true)]
  [TestCase(1, true)]
  public void Evaluation_Budget(int over, bool folded) {
    var module = Legalized(Fib + "public static int G() => Fib(6);");
    var needed = Enumerable.Range(1, 1000).First(steps => {
      try {
        IrInterpreter.Call(module with { Globals = [] }, "Fib", [6], steps);
        return true;
      } catch (IrBudgetException) {
        return false;
      }
    });
    var notes = new List<string>();
    var result = new O0002CallEvaluation(needed + over).Run(module, _ => true, (_, _) => { }, notes.Add);
    Assert.That(Calls(result, "G", "Fib"), Is.EqualTo(folded ? 0 : 1));
    Assert.That(notes, Has.Count.EqualTo(folded ? 0 : 1));
  }

  /// <summary>Test design 4: a pure call that traps - a division by a constant zero - stays, and traps where it did.</summary>
  [Test]
  public void Evaluation_KeepsACallThatTraps() {
    const string methods = "static int D(int x) => 10 / x; public static int G() => D(0) + 1;";
    var module = Legalized(methods);
    var result = Optimized(module);
    Assert.That(Ids(result), Has.None.EqualTo("O0002"));
    SameOutcomes(methods, module, result.Module, new PrototypeCall("G", []));
    Assert.That(PrototypeFixture.InterpretedOutcome(result.Module, new PrototypeCall("G", [])), Does.StartWith("trap:"));
  }

  /// <summary>What is not evaluated: a call that reads a global, one with an argument that is no constant, one whose value nothing uses, one of floating point, one with a result of floating point.</summary>
  [TestCase("static int g = 4; static int R(int x) => x + g; public static int G(int k) => R(1) + k;")]
  [TestCase("static int R(int x) => x + 1; public static int G(int k) => R(k);")]
  [TestCase("static int R(int x) => x + 1; public static int G(int k) { R(1); return k; }")]
  [TestCase("static int R(int x) => (int)Math.Sqrt(x); public static int G(int k) => R(16) + k;")]
  [TestCase("static double R(int x) => x * 0.5; public static int G(int k) => (int)R(4) + k;")]
  [TestCase("static int R(int x) => double.IsNaN(x * 0.5) ? 1 : 0; public static int G(int k) => R(4) + k;")]
  public void Evaluation_KeepsWhatItCannotEvaluate(string methods) {
    var module = Legalized(methods);
    var result = new O0002CallEvaluation().Run(module, _ => true, (_, _) => { });
    Assert.That(Calls(result, "G", "R"), Is.EqualTo(1));
    SameOutcomes(methods, module, Optimized(module).Module, new PrototypeCall("G", [3]));
  }

  /// <summary>Arguments and results of every integer type and of truth values cross into the interpreter and back.</summary>
  [Test]
  public void Evaluation_EveryIntegerType() {
    const string methods = """
      static sbyte A(sbyte x) => (sbyte)(x - 1); static byte B(byte x) => (byte)(x + 1); static short C(short x) => (short)(x * 2);
      static ushort D(ushort x) => (ushort)(x + 3); static uint E(uint x) => x * 3u; static long L(long x) => x << 40;
      static ulong U(ulong x) => x * 5ul; static bool N(bool x) => !x; static char H(char c) => (char)(c + 1);
      public static long G() => A(-128) + B(255) + C(300) + D(65535) + (long)E(4000000000u) + L(3) + (long)U(ulong.MaxValue / 7) + (N(false) ? 1 : 0) + H('a');
      """;
    var module = Legalized(methods);
    var result = Optimized(module);
    Assert.That(IrTree.Nodes(Function(result.Module, "G").Body).OfType<IrCall>(), Is.Empty);
    SameOutcomes(methods, module, result.Module, new PrototypeCall("G", []));
  }

  // ---------------------------------------------------------------- O0464 call merging (row 5)

  private const string H = "public static int H(int[] a, int k) { Func<int> lim = () => k * 2; int s = 0; for (int i = 0; i < a.Length; i++) if (a[i] < lim() && a[i] > -lim()) s++; return s; }";

  /// <summary>Scenario "Identical read-only calls are merged": each loop pass computes <c>lim</c> once, and <c>H([1, 5, -3], 2)</c> is 2.</summary>
  [Test]
  public void Merging_TheScenario() {
    var module = Legalized(H);
    var result = Optimized(module);
    Assert.That(Ids(result), Does.Contain("O0464"));
    Assert.That(Calls(result.Module, "H", "H_lim"), Is.EqualTo(1));
    Assert.That(IrTree.Nodes(Function(result.Module, "H").Body).OfType<IrBind>().Select(bind => bind.Value), Has.One.InstanceOf<IrCall>());
    SameOutcomes(H, module, result.Module, new PrototypeCall("H", [new[] { 1, 5, -3 }, 2]), new PrototypeCall("H", [Array.Empty<int>(), 0]), new PrototypeCall("H", [new[] { 0, -1, 1 }, 1]));
    var (text, _) = SnippetTranslation.Translate(Source(H), "csharp");
    Assert.That(Regex.Matches(text, @"H_lim\(k\)"), Has.Count.EqualTo(1), text);
    Assert.That(GeneratedCSharp.Compile(text, false).Outcome(new PrototypeCall("H", [new[] { 1, 5, -3 }, 2])), Does.StartWith("2 |"), text);
  }

  private const string Globals = "static int g = 1; static int h; static int Get(int x) => g + x; static int SetG() { g++; return 0; } static int SetH() { h++; return 0; } ";

  /// <summary>Test design 5: a write to what the function reads between two calls keeps both; an unrelated write and no write merge them.</summary>
  [TestCase("public static int M(int x) => Get(x) + SetG() + Get(x);", 2)]
  [TestCase("public static int M(int x) => Get(x) + SetH() + Get(x);", 1)]
  [TestCase("public static int M(int x) => Get(x) * Get(x);", 1)]
  [TestCase("public static int M(int x) { int y = x; return Get(y) + y++ + Get(y); }", 2)]
  [TestCase("public static int M(int x) { int y = x; return Get(y) + x++ + Get(y); }", 1)]
  public void Merging_Barrier(string method, int calls) {
    var methods = Globals + method;
    var module = Legalized(methods);
    var merged = new O0464CallMerging().Run(module, _ => true, (_, _) => { });
    Assert.That(Calls(merged, "M", "Get"), Is.EqualTo(calls));
    Assert.That(IrVerifier.Verify(merged), Is.Empty);
    var calls2 = new[] { new PrototypeCall("M", [3]), new PrototypeCall("M", [-7]) };
    SameAsBefore(module, merged, calls2);
    SameAsBefore(module, Optimized(module).Module, calls2);
  }

  /// <summary>
  /// A later call merges only where the first always runs before it: not one in a branch after one
  /// outside it in the other order, but a branch after a call outside it, and two in the same branch.
  /// </summary>
  [TestCase("public static int M(int x) => (x > 0 ? Get(x) : 0) + Get(x);", 2)]
  [TestCase("public static int M(int x) => Get(x) + (x > 0 ? Get(x) : 0);", 1)]
  [TestCase("public static int M(int x) => x > 0 ? Get(x) + Get(x) : 0;", 1)]
  [TestCase("public static int M(int x) => x > 0 && Get(x) > 1 || Get(x) > 2 ? 1 : 0;", 2)]
  [TestCase("public static int M(int x) => Get(x) > 1 && Get(x) > 2 ? 1 : 0;", 1)]
  [TestCase("public static int M(int x) { int? n = x > 0 ? x : null; return (n ?? Get(x)) + Get(x); }", 2)]
  public void Merging_OnlyWhereTheFirstAlwaysRuns(string method, int calls) {
    var methods = Globals + method;
    var module = Legalized(methods);
    var merged = new O0464CallMerging().Run(module, _ => true, (_, _) => { });
    Assert.That(Calls(merged, "M", "Get"), Is.EqualTo(calls));
    SameAsBefore(module, merged, new PrototypeCall("M", [3]), new PrototypeCall("M", [-7]), new PrototypeCall("M", [1]));
    var (text, _) = SnippetTranslation.Translate(Source(methods), "csharp", "--profile", "net2.0");
    Assert.That(GeneratedCSharp.ComplaintsFor(text, "net2.0", "2"), Is.Empty, text);
  }

  /// <summary>Never merged: a function that writes, one that makes a new array, a call through a function value, arguments that may trap.</summary>
  [TestCase("public static int M(int x) => SetH() + SetH() + x;", "SetH", 2)]
  [TestCase("static int[] Mk(int n) => new int[n]; public static int M(int x) => Mk(1).Length + Mk(1).Length + x;", "Mk", 2)]
  [TestCase("public static int M(int x) => Get(10 / x) + Get(10 / x);", "Get", 2)]
  public void Merging_NeverWhatMayNot(string method, string callee, int calls) {
    var methods = Globals + method;
    var module = Legalized(methods);
    var merged = new O0464CallMerging().Run(module, _ => true, (_, _) => { });
    Assert.That(Calls(merged, "M", callee), Is.EqualTo(calls));
    SameAsBefore(module, Optimized(module).Module, new PrototypeCall("M", [3]), new PrototypeCall("M", [0]));
  }

  /// <summary>Scenario "A writing lambda is never merged or deleted": <c>W()</c> keeps both calls and returns 2.</summary>
  [Test]
  public void Merging_AWritingLambdaKeepsBothCalls() {
    const string methods = "public static int W() { int n = 0; Func<int> inc = () => ++n; inc(); inc(); return n; }";
    var module = Legalized(methods);
    var result = Optimized(module);
    Assert.That(IrTree.Nodes(Function(result.Module, "W").Body).OfType<IrInvoke>().Count(), Is.EqualTo(2));
    SameOutcomes(methods, module, result.Module, new PrototypeCall("W", []));
  }

  /// <summary>Calls merged inside the arguments of a call that is merged in turn keep their variable where a call outside reads it.</summary>
  [Test]
  public void Merging_Nested() {
    const string methods = "static int Sq(int x) => x * x; static int Two(int a, int b) => a * 10 + b; public static int M(int x) => Two(Sq(x), x) + Sq(x) + Two(Sq(x), x) + Sq(x);";
    var module = Legalized(methods);
    var merged = new O0464CallMerging().Run(module, _ => true, (_, _) => { });
    Assert.That((Calls(merged, "M", "Two"), Calls(merged, "M", "Sq")), Is.EqualTo((1, 1)));
    SameOutcomes(methods, module, merged, new PrototypeCall("M", [3]));
  }

  /// <summary>
  /// Every kind of statement whose expression is evaluated in one go has its calls merged, a loop's
  /// condition each time it is evaluated; the store into an array element has three expressions, and
  /// a call is merged within one of them only.
  /// </summary>
  [Test]
  public void Merging_InEveryKindOfStatement() {
    const string methods = Globals + """
      static int[] arr = new int[4];
      public static int M(int x) {
        int a = Get(x) + Get(x);
        a = Get(a) - Get(a);
        arr[Get(1) & 3] = Get(1);
        g = Get(2) + Get(2);
        while (Get(a) * Get(a) < 50) a++;
        do { a--; } while (Get(a) > Get(a) + 1);
        for (int i = 0; Get(i) < Get(i) + 1 && i < 2; i++) a += i;
        switch (Get(x) * Get(x)) { case 4: a += 100; break; default: a += 1; break; }
        if (Get(a) == Get(a) + 1) throw new InvalidOperationException();
        return a + arr[0] + arr[1] + arr[2] + arr[3];
      }
      """;
    var module = Legalized(methods);
    var merged = new O0464CallMerging().Run(module, _ => true, (_, _) => { });
    Assert.That(IrTree.Nodes(Function(merged, "M").Body).OfType<IrBind>().Count(), Is.EqualTo(8));
    Assert.That(IrVerifier.Verify(merged), Is.Empty);
    SameAsBefore(module, merged, new PrototypeCall("M", [1]), new PrototypeCall("M", [-3]));
  }

  /// <summary>
  /// An async function (#1330) is the worst case: its call is never evaluated, merged or removed, and
  /// neither is a native call (#1332).
  /// </summary>
  [Test]
  public void AsyncAndNativeCalls_AreNeverEvaluatedMergedOrRemoved() {
    const string methods = """
      [System.Runtime.InteropServices.DllImport("libc")] static extern int abs(int x);
      static async System.Threading.Tasks.Task<int> A(int x) { return x * 2; }
      static async System.Threading.Tasks.Task<int> Unused(int x) { return x; }
      public static async System.Threading.Tasks.Task<int> G(int k) { await Unused(k); return await A(1) + await A(1) + await A(k) + await A(k); }
      public static int N(int k) => abs(k) + abs(k);
      """;
    var module = Legalized(methods);
    var effects = IrEffects.Of(module);
    Assert.That((effects.Function("A").Kinds, effects.Function("G").Kinds), Is.EqualTo(((IrEffectKinds)31, (IrEffectKinds)31)));
    Assert.That(effects.Function("N").WritesEverything, Is.True);
    var result = Optimized(module);
    Assert.That((Calls(result.Module, "G", "A"), Calls(result.Module, "G", "Unused")), Is.EqualTo((4, 1)));
    Assert.That(IrTree.Nodes(Function(result.Module, "N").Body).OfType<IrForeignCall>().Count(), Is.EqualTo(2));
    Assert.That(Ids(result), Has.None.EqualTo("O0002").And.None.EqualTo("O0464"));
  }

  // ---------------------------------------------------------------- O0005 dead-code removal of calls

  /// <summary>An unused call of a function that writes nothing, cannot trap and ends goes, and so does an unread variable it initialises; any other call stays.</summary>
  [TestCase("static int R(int x) => x * x + g; public static int U(int x) { R(x); return x; }", 0)]
  [TestCase("static int R(int x) => x * x; public static int U(int x) { int t = R(x); return x; }", 0)]
  [TestCase("static int R(int x) => 10 / x; public static int U(int x) { R(x); return x; }", 1)]
  [TestCase("static int R(int x) => R(x - 1); public static int U(int x) { if (x > 5) R(x); return x; }", 1)]
  [TestCase("static int R(int x) { h = x; return x; } public static int U(int x) { R(x); return x + h; }", 1)]
  public void DeadCode_RemovesUnusedCalls(string method, int calls) {
    var methods = "static int g = 2; static int h = 1; static int Use() => g + h; " + method;
    var module = Legalized(methods);
    var result = new O0005DeadCode().Run(module, _ => true, (_, _) => { });
    Assert.That(Calls(result, "U", "R"), Is.EqualTo(calls));
    SameOutcomes(methods, module, result, new PrototypeCall("U", [3]), new PrototypeCall("U", [0]));
  }

  /// <summary>A variable an expression binds keeps its declaration though nothing reads it: the binding assigns it.</summary>
  [Test]
  public void DeadCode_KeepsABoundVariable() {
    var t = new IrLocal("t", IrType.I32);
    var x = new IrLocal("x", IrType.I32);
    var f = new IrFunction("F", true, IrType.I32, [x], [t], new IrBlock([
      new IrDeclare(t, new IrConst(IrType.I32, 0)),
      new IrReturn(new IrBinary(IrBinaryOp.Add, new IrBind(t, new IrLocalRef(x)), new IrConst(IrType.I32, 1)))]));
    var module = new IrModule("M", [f]);
    var result = new O0005DeadCode().Run(module, _ => true, (_, _) => { });
    Assert.That(IrVerifier.Verify(result), Is.Empty);
    Assert.That(IrInterpreter.Call(result, "F", [4]), Is.EqualTo(5));
  }

  /// <summary>Propagation replaces no read of a variable the same expression binds or steps first: with <c>t</c> known 0, <c>(t = p) + t</c> is <c>2p</c> and <c>++t + t</c> is <c>4p + 2</c>.</summary>
  [Test]
  public void Propagation_LeavesAVariableTheExpressionChanges() {
    var p = new IrLocal("p", IrType.I32);
    var t = new IrLocal("t", IrType.I32);
    var x = new IrLocal("x", IrType.I32);
    IrExpr Ref(IrLocal local) => new IrLocalRef(local);
    var f = new IrFunction("F", true, IrType.I32, [p], [t, x], new IrBlock([
      new IrDeclare(t, new IrConst(IrType.I32, 0)),
      new IrDeclare(x, new IrBinary(IrBinaryOp.Add, new IrBind(t, Ref(p)), Ref(t))),
      new IrReturn(new IrBinary(IrBinaryOp.Add, Ref(x), new IrBinary(IrBinaryOp.Add, new IrStep(Ref(t), true, false, false), Ref(t))))]));
    var module = new IrModule("M", [f]);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    var result = new O0003ConstantPropagation().Run(module, _ => true, (_, _) => { });
    Assert.That((IrInterpreter.Call(result, "F", [4]), IrInterpreter.Call(module, "F", [4])), Is.EqualTo((18, 18)));
  }

  // ---------------------------------------------------------------- switches and classification (rows 6 and 2)

  /// <summary>Test design 6: each pass <c>--disable-opt</c> names, and every pass at <c>--opt none</c>, changes nothing.</summary>
  [TestCase("O0002")]
  [TestCase("O0464")]
  [TestCase("O0005")]
  public void Switch_DisabledPassNeverFires(string id) {
    const string methods = Fib + "static int Sq(int x) => x * x; public static int G(int k) { Sq(k); return Fib(10) + Sq(k) * Sq(k); }";
    var module = Legalized(methods);
    var enabled = Optimized(module);
    Assert.That(Ids(enabled), Is.SupersetOf(new[] { "O0002", "O0464" }));
    var disabled = Optimized(module, id);
    Assert.That(Ids(disabled), Has.None.EqualTo(id));
    SameOutcomes(methods, module, disabled.Module, new PrototypeCall("G", [3]));
    var none = IrPassPipeline.Default.Run(module, new OptimizerOptions { Level = OptimizationLevel.None });
    Assert.That((none.Module, none.Applications.Count), Is.EqualTo((module, 0)));
  }

  /// <summary>The pipeline runs evaluation after folding and merging after evaluation, and the passes are switched off by their numbers on the command line.</summary>
  [Test]
  public void Switch_CommandLine() {
    var ids = IrPassPipeline.Default.Ids.ToList();
    Assert.That(ids.IndexOf("O0001") < ids.IndexOf("O0002") && ids.IndexOf("O0002") < ids.IndexOf("O0464") && ids.IndexOf("O0464") < ids.IndexOf("O0005"), Is.True);
    var (text, _) = SnippetTranslation.Translate(Source(Fib + "public static int G() => Fib(10);"), "csharp", "--disable-opt", "O0002,O0464");
    Assert.That(text, Does.Contain("Fib(10)"));
  }

  /// <summary><c>--explain-opt</c> names the class of every function of a scope: what it captures, whether it was lifted, what it does.</summary>
  [Test]
  public void Classification_IsExplained() {
    const string methods = H + " public static int W() { int n = 0; Func<int> inc = () => ++n; inc(); inc(); return n; } public static int T(int[] a) { Func<int, int> at = i => a[i]; return at(0) + at(1); }";
    var (_, error) = SnippetTranslation.Translate(Source(methods), "csharp", "--explain-opt");
    Assert.That(error, Does.Contain("opt O0463 lambda specialisation: 'H_lim' of 'H' is static, lifted, pure"));
    Assert.That(error, Does.Contain("opt O0463 lambda specialisation: 'inc' of 'W' is capturing, a closure, writes"));
    Assert.That(error, Does.Contain("opt O0463 lambda specialisation: 'T_at' of 'T' is static, lifted, reads, traps"));
    Assert.That(Regex.Matches(error, "'H_lim' of 'H'"), Has.Count.EqualTo(1));
    Assert.That(error, Does.Match(@"opt O0464 call merging: 'H' statement [\d.]+"));
  }

  /// <summary>A function of a scope recorded without its effects - by a module made elsewhere - is unclassified; one whose closure is gone is the worst case.</summary>
  [Test]
  public void Classification_WithoutEffects() {
    var module = new IrModule("M", [new IrFunction("F", true, IrType.I32, [], [], new IrBlock([new IrReturn(new IrConst(IrType.I32, 1))]))]) {
      ScopeFunctions = [new IrScopeFunction("F", "gone", IrCaptureClass.Capturing, false)]
    };
    Assert.That(Optimized(module).Notes, Has.One.EqualTo("O0463 lambda specialisation: 'gone' of 'F' is capturing, a closure, unclassified"));
  }

  // ---------------------------------------------------------------- the fixtures (row 7)

  private static IEnumerable<TestCaseData> FixtureCalls() {
    foreach (var (file, _, call) in FunctionsFixtures.Calls())
      yield return new TestCaseData("functions", file, call).SetName($"Fixture_functions_{file}.{call}");

    foreach (var (file, _, call) in ExceptionFixtures.Calls())
      yield return new TestCaseData("exceptions", file, call).SetName($"Fixture_exceptions_{file}.{call}");

    foreach (var (file, _, call) in DispatchFixtures.Calls())
      yield return new TestCaseData("dispatch", file, call).SetName($"Fixture_dispatch_{file}.{call}");

    foreach (var (file, _, call) in CollectionFixtures.Calls())
      yield return new TestCaseData("collections", file, call).SetName($"Fixture_collections_{file}.{call}");
  }

  private static readonly Dictionary<(string, string), (IrModule Before, IrModule After)> _fixtures = [];

  private static (IrModule Before, IrModule After) Fixture(string kind, string file) {
    lock (_fixtures) {
      if (_fixtures.TryGetValue((kind, file), out var known))
        return known;

      var imported = kind switch {
        "functions" => FunctionsFixtures.Imported(file),
        "exceptions" => ExceptionFixtures.Imported(file),
        "dispatch" => DispatchFixtures.Imported(file),
        _ => CollectionFixtures.Imported(file)
      };
      var legalized = CSharpEmitter.Legalize(imported, () => PrecomputedImplementations.Module, CSharpProfile.Default, FloatMode.Accurate);
      return _fixtures[(kind, file)] = (legalized, Optimized(legalized).Module);
    }
  }

  /// <summary>Test design 7: the optimised module of every fixture computes what the unoptimised one computes, traps included.</summary>
  [TestCaseSource(nameof(FixtureCalls))]
  public void Fixture_OptimisedAgreesWithUnoptimised(string kind, string file, PrototypeCall call) {
    var (before, after) = Fixture(kind, file);
    Assert.That(PrototypeFixture.InterpretedOutcome(after, call), Is.EqualTo(PrototypeFixture.InterpretedOutcome(before, call)));
  }

}
