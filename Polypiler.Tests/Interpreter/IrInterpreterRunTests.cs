using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Interpreter;

/// <summary>
/// The non-throwing entry point of the reference interpreter (#119): <see cref="IrInterpreter.Run"/> with its four
/// outcomes, the budgets, the probes, the effect trace through an external model, and the diagnostics PLY0120 to
/// PLY0124, row by row of the test design in the tree form of ADR 0009.
/// </summary>
[TestFixture]
public sealed class IrInterpreterRunTests {

  private const string Pick = "public static uint Pick(bool c, uint a, uint b) { if (c) return a; return b; }";

  private static IrModule Import(string methods, string usings = "") => PrototypeFixture.Import("Snippet.cs", usings + PrototypeFixture.Wrap(methods));

  private static ImmutableArray<IrValue> Values(params IrValue[] values) => [.. values];

  private static IrValue U32(uint value) => IrValue.Integer(IrType.U32, value);

  private static IrValue I32(int value) => IrValue.Integer(IrType.I32, value);

  private static ExecutionResult Run(string methods, string function, params IrValue[] arguments) => IrInterpreter.Run(Import(methods), function, [.. arguments]);

  /// <summary>Test design 1: the diamond returns the value of the branch taken.</summary>
  [TestCase(true, 10u)]
  [TestCase(false, 20u)]
  public void TheDiamond_ReturnsTheSelectedValue(bool condition, uint expected) {
    var result = Run(Pick, "Pick", IrValue.Bool(condition), U32(10), U32(20));
    Assert.Multiple(() => {
      Assert.That(result.Outcome, Is.EqualTo(ExecutionOutcome.Returned));
      Assert.That(result.Value, Is.EqualTo(U32(expected)));
      Assert.That(result.Trap, Is.Null);
      Assert.That(result.At, Is.Null);
      Assert.That(result.Diagnostic, Is.Null);
      Assert.That(result.Trace, Is.Empty);
      Assert.That(result.Steps, Is.Positive);
      Assert.That(result.RecordedValues, Is.Null);
    });
  }

  /// <summary>Test design 2: each structural statement computes in a run what the exception API computes.</summary>
  [TestCase("public static int F(int n) { int s = 0; while (n > 0) { s += n; n--; } return s; }", 10, 55)]
  [TestCase("public static int F(int n) { int s = 0; do { s += n; n--; } while (n > 0); return s; }", 0, 0)]
  [TestCase("public static int F(int n) { int s = 0; for (int i = 0; i < n; i++) { if (i == 3) continue; if (i == 7) break; s += i; } return s; }", 10, 18)]
  [TestCase("public static int F(int n) { switch (n) { case 1: return 10; case 2: return 20; default: return -1; } }", 2, 20)]
  [TestCase("public static int F(int n) => n > 0 ? G(n) : 0; private static int G(int n) => n * 2;", 4, 8)]
  public void EveryStructuralStatement_AgreesWithTheExceptionApi(string methods, int argument, int expected) {
    var module = Import(methods);
    Assert.That(IrInterpreter.Call(module, "F", [argument]), Is.EqualTo(expected));
    Assert.That(IrInterpreter.Run(module, "F", Values(I32(argument))).Value, Is.EqualTo(I32(expected)));
  }

  /// <summary>Test design 3: recursion.</summary>
  [Test]
  public void Recursion_Returns()
    => Assert.That(Run("public static int Count(int n) => n == 0 ? 0 : 1 + Count(n - 1);", "Count", I32(12)).Value, Is.EqualTo(I32(12)));

  /// <summary>Test design 4: a swap through a temporary exchanges the two values, so the loop ends with 2, not 1.</summary>
  [Test]
  public void ASwapLoop_ExchangesBothValues()
    => Assert.That(Run("public static uint F() { uint x = 1, y = 2; for (int i = 0; i < 1; i++) { uint t = x; x = y; y = t; } return x; }", "F").Value, Is.EqualTo(U32(2)));

  /// <summary>Test design 5: a trap in the callee is handled in the caller.</summary>
  [Test]
  public void ATrapInTheCallee_IsHandledInTheCaller() {
    var result = Run("public static int F(int d) { try { return G(d); } catch (System.DivideByZeroException) { return -1; } } private static int G(int d) => 1 / d;", "F", I32(0));
    Assert.That(result.Outcome, Is.EqualTo(ExecutionOutcome.Returned));
    Assert.That(result.Value, Is.EqualTo(I32(-1)));
  }

  /// <summary>Test design 10: a trap that leaves the function ends the run Trapped, in the function it was raised in.</summary>
  [Test]
  public void AnUncaughtTrap_EndsTheRunTrapped() {
    var result = Run("public static int F(int d) => G(d) + 1; private static int G(int d) => 1 / d;", "F", I32(0));
    Assert.Multiple(() => {
      Assert.That(result.Outcome, Is.EqualTo(ExecutionOutcome.Trapped));
      Assert.That(result.Trap, Is.EqualTo(IrTrap.DivideByZero));
      Assert.That(result.Thrown, Is.EqualTo(IrExceptionClass.Of(IrTrap.DivideByZero)));
      Assert.That(result.Message, Is.EqualTo("divide by zero"));
      Assert.That(result.At, Is.EqualTo(new InstructionRef("G", null)));
      Assert.That(result.Value, Is.Null);
      Assert.That(result.MaxFrameDepth, Is.EqualTo(2));
    });
  }

  /// <summary>Test design 10: an object the program throws and nothing catches ends the run Trapped, with its class and no trap.</summary>
  [Test]
  public void AnUncaughtThrow_EndsTheRunTrappedWithItsClass() {
    var result = Run("public static int F() => throw new System.InvalidOperationException(\"no\");", "F");
    Assert.Multiple(() => {
      Assert.That(result.Outcome, Is.EqualTo(ExecutionOutcome.Trapped));
      Assert.That(result.Trap, Is.Null);
      Assert.That(result.Thrown!.Name, Does.Contain("InvalidOperationException"));
      Assert.That(result.Message, Is.EqualTo("no"));
      Assert.That(result.At!.Function, Is.EqualTo("F"));
    });
  }

  /// <summary>Test design 6: a function of exactly N steps aborts with N - 1, returns with N and N + 1.</summary>
  [Test]
  public void TheStepBudget_HoldsExactly() {
    var module = Import("public static int F() { int s = 0; for (int i = 0; i < 100; i++) s += i; return s; }");
    var steps = IrInterpreter.Run(module, "F", []).Steps;
    var under = IrInterpreter.Run(module, "F", [], new() { MaxSteps = steps - 1 });
    Assert.Multiple(() => {
      Assert.That(under.Outcome, Is.EqualTo(ExecutionOutcome.Aborted));
      Assert.That(under.Diagnostic!.Code, Is.EqualTo("PLY0121"));
      Assert.That(under.Diagnostic.Message, Is.EqualTo($"step budget of {steps - 1} exceeded in '@F'"));
      Assert.That(under.Steps, Is.EqualTo(steps - 1));
      Assert.That(under.At!.Function, Is.EqualTo("F"));
      Assert.That(IrInterpreter.Run(module, "F", [], new() { MaxSteps = steps }).Outcome, Is.EqualTo(ExecutionOutcome.Returned));
      Assert.That(IrInterpreter.Run(module, "F", [], new() { MaxSteps = steps + 1 }).Steps, Is.EqualTo(steps));
    });
  }

  /// <summary>The scenario of the endless loop: aborted with PLY0121 after exactly the budget.</summary>
  [Test]
  public void AnEndlessLoop_EndsAtTheStepBudget() {
    var result = IrInterpreter.Run(Import("public static void Spin() { while (true) { } }"), "Spin", [], new() { MaxSteps = 1000 });
    Assert.That(result.Outcome, Is.EqualTo(ExecutionOutcome.Aborted));
    Assert.That(result.Diagnostic!.Code, Is.EqualTo("PLY0121"));
    Assert.That(result.Steps, Is.EqualTo(1000));
  }

  /// <summary>A budget of no steps aborts at the call itself, which has no frame yet.</summary>
  [Test]
  public void ABudgetOfNoSteps_AbortsBeforeTheFirstFrame() {
    var result = IrInterpreter.Run(Import(Pick), "Pick", [IrValue.Bool(true), U32(1), U32(2)], new() { MaxSteps = 0 });
    Assert.That(result.Diagnostic!.Message, Is.EqualTo("step budget of 0 exceeded in '@'"));
    Assert.That(result.At, Is.Null);
    Assert.That(result.Steps, Is.Zero);
    Assert.That(result.MaxFrameDepth, Is.Zero);
  }

  private const string Depth = "public static int D(int n) => n <= 1 ? 1 : 1 + D(n - 1);";

  /// <summary>Test design 7: recursion of the default depth returns, one frame more aborts.</summary>
  [Test, Category("Slow")]
  public void TheCallDepth_HoldsAtItsLimit() {
    var module = Import(Depth);
    var limit = InterpreterOptions.DefaultMaxCallDepth;
    var deepest = IrInterpreter.Run(module, "D", [I32(limit)]);
    var beyond = IrInterpreter.Run(module, "D", [I32(limit + 1)]);
    Assert.Multiple(() => {
      Assert.That(deepest.Value, Is.EqualTo(I32(limit)));
      Assert.That(deepest.MaxFrameDepth, Is.EqualTo(limit));
      Assert.That(beyond.Outcome, Is.EqualTo(ExecutionOutcome.Aborted));
      Assert.That(beyond.Diagnostic!.Code, Is.EqualTo("PLY0121"));
      Assert.That(beyond.Diagnostic.Message, Is.EqualTo($"call depth {limit} exceeded"));
      Assert.That(beyond.At!.Function, Is.EqualTo("D"));
    });
  }

  /// <summary>Test design 19: the frame depth of no call, one call and recursion of depth 5.</summary>
  [TestCase("public static int F() => 1;", 1)]
  [TestCase("public static int F() => G(); private static int G() => 2;", 2)]
  [TestCase("public static int F() => D(5); " + Depth, 6)]
  [TestCase("public static int F() => G() + G(); private static int G() => 2;", 2)]
  public void TheFrameDepth_IsTheMostFramesAtOnce(string methods, int expected)
    => Assert.That(Run(methods, "F").MaxFrameDepth, Is.EqualTo(expected));

  /// <summary>The probes of the scenario: @Pick has one frame, @Outer two, and neither allocates.</summary>
  [Test]
  public void TheProbes_ReportFramesAndAllocations() {
    var module = Import(Pick + " public static uint Outer() => Pick(true, 1, 2);");
    var pick = IrInterpreter.Run(module, "Pick", [IrValue.Bool(true), U32(10), U32(20)]);
    var outer = IrInterpreter.Run(module, "Outer", []);
    Assert.That((pick.MaxFrameDepth, outer.MaxFrameDepth, pick.Allocations, outer.Allocations), Is.EqualTo((1, 2, 0L, 0L)));
  }

  /// <summary>Every allocating node counts once each time it runs: arrays, objects, arrays of values, collections.</summary>
  [TestCase("public static int F() { int n = 0; for (int i = 0; i < 3; i++) n += new int[i].Length; return n; }", 3)]
  [TestCase("public static int F() { int[] a = { 1, 2 }; return a.Length; }", 1)]
  [TestCase("public static int F() { var l = new System.Collections.Generic.List<int>(); l.Add(1); return l.Count; }", 1)]
  [TestCase("public static int F() => new C().V; private sealed class C { public int V = 4; }", 1)]
  public void TheAllocations_CountEachAllocatingNode(string methods, long expected)
    => Assert.That(Run(methods, "F").Allocations, Is.EqualTo(expected));

  /// <summary>Test design 8: a switch over a byte reaches the smallest and the largest case.</summary>
  [TestCase((byte)0, 1)]
  [TestCase((byte)255, 2)]
  [TestCase((byte)7, 0)]
  public void ASwitchOnAByte_ReachesItsBoundaryCases(byte selector, int expected)
    => Assert.That(Run("public static int F(byte b) { switch (b) { case 0: return 1; case 255: return 2; default: return 0; } }", "F", IrValue.Integer(IrType.U8, selector)).Value, Is.EqualTo(I32(expected)));

  /// <summary>Test design 9: a function of no parameters returning nothing returns with no value.</summary>
  [Test]
  public void AVoidFunction_ReturnsNoValue() {
    var result = Run("public static void F() { }", "F");
    Assert.That(result.Outcome, Is.EqualTo(ExecutionOutcome.Returned));
    Assert.That(result.Value, Is.Null);
  }

  /// <summary>Test design 12: arguments of the wrong number and of the wrong type are refused with PLY0122; nothing runs.</summary>
  [Test]
  public void WrongArguments_AreRefused() {
    var module = Import(Pick);
    var few = IrInterpreter.Run(module, "Pick", [IrValue.Bool(true), U32(10)]);
    var narrow = IrInterpreter.Run(module, "Pick", [IrValue.Bool(true), IrValue.Integer(IrType.U16, 10), U32(20)]);
    var none = IrInterpreter.Run(module, "Pick", default);
    Assert.Multiple(() => {
      Assert.That(few.Outcome, Is.EqualTo(ExecutionOutcome.Aborted));
      Assert.That(few.Diagnostic!.Code, Is.EqualTo("PLY0122"));
      Assert.That(few.Message, Is.EqualTo("'@Pick' expects (bool, u32, u32), got (bool, u32)"));
      Assert.That(narrow.Message, Is.EqualTo("'@Pick' expects (bool, u32, u32), got (bool, u16, u32)"));
      Assert.That(none.Message, Is.EqualTo("'@Pick' expects (bool, u32, u32), got ()"));
      Assert.That(few.Steps, Is.Zero);
    });
  }

  /// <summary>An array crosses as a reference, and a reference that is no value of the parameter's type is refused.</summary>
  [Test]
  public void AReferenceArgument_CrossesAsTheHostValue() {
    var module = Import("public static int F(int[] a) { a[0] = 9; return a.Length; }");
    var array = new[] { 1, 2, 3 };
    var type = IrType.Array(IrType.I32);
    Assert.That(IrInterpreter.Run(module, "F", [IrValue.Referring(type, array)]).Value, Is.EqualTo(I32(3)));
    Assert.That(array[0], Is.EqualTo(9));
    Assert.That(IrInterpreter.Run(module, "F", [IrValue.Referring(type, new uint[1])]).Diagnostic!.Code, Is.EqualTo("PLY0122"));
  }

  /// <summary>A value that is no scalar is returned as a reference.</summary>
  [Test]
  public void AReferenceResult_IsReturnedAsTheHostValue() {
    var result = Run("public static int[] F() => new int[] { 4, 5 };", "F");
    Assert.That(result.Value!.Type, Is.EqualTo(IrType.Array(IrType.I32)));
    Assert.That(result.Value.Reference, Is.EqualTo(new[] { 4, 5 }));
  }

  /// <summary>Every scalar type returns its bit pattern.</summary>
  [Test]
  public void EveryScalarResult_IsItsBitPattern() {
    Assert.Multiple(() => {
      Assert.That(Run("public static long F() => -1;", "F").Value!.Bits, Is.EqualTo(new System.Numerics.BigInteger(ulong.MaxValue)));
      Assert.That(Run("public static ulong F() => ulong.MaxValue;", "F").Value, Is.EqualTo(IrValue.Integer(IrType.U64, ulong.MaxValue)));
      Assert.That(Run("public static double F() => 0.5;", "F").Value, Is.EqualTo(IrValue.F64(0.5)));
      Assert.That(Run("public static float F() => 0.5f;", "F").Value, Is.EqualTo(IrValue.F32(0.5f)));
      Assert.That(Run("public static bool F() => true;", "F").Value, Is.EqualTo(IrValue.Bool(true)));
      var big = new IrModule("M", [new IrFunction("f", true, IrType.BigInt, [], [], new IrBlock([new IrReturn(new IrBigConst(System.Numerics.BigInteger.One << 70))]))]);
      Assert.That(IrInterpreter.Run(big, "f", []).Value!.Bits, Is.EqualTo(System.Numerics.BigInteger.One << 70));
    });
  }

  /// <summary>Test design 14: a module that does not verify is not run: PLY0124 with the verifier's diagnostics.</summary>
  [Test]
  public void AModuleThatDoesNotVerify_IsNotRun() {
    var module = new IrModule("M", [new IrFunction("f", true, IrType.I32, [], [], new IrBlock([new IrReturn(new IrConst(IrType.Bool, 1))]))]);
    var result = IrInterpreter.Run(module, "f", []);
    Assert.Multiple(() => {
      Assert.That(result.Outcome, Is.EqualTo(ExecutionOutcome.Aborted));
      Assert.That(result.Diagnostic!.Code, Is.EqualTo("PLY0124"));
      Assert.That(result.Message, Is.EqualTo($"module does not verify: {result.Diagnostics.Length} diagnostics"));
      Assert.That(result.Diagnostics, Is.EqualTo(IrVerifier.Verify(module)));
      Assert.That(result.Steps, Is.Zero);
    });
  }

  /// <summary>Test design 15: a local read before anything was assigned to it aborts with PLY0120 instead of inventing a value.</summary>
  [Test]
  public void AnUndefinedLocal_AbortsWhenRead() {
    var x = new IrLocal("x", IrType.U32);
    var module = new IrModule("M", [new IrFunction("f", true, IrType.U32, [], [x], new IrBlock([
      new IrDeclare(x, null),
      // Some path assigns x, so the verifier lets the read stand; the path the run takes does not.
      new IrIf(new IrConst(IrType.Bool, 0), new IrBlock([new IrAssign(x, new IrConst(IrType.U32, 1))]), null),
      new IrReturn(new IrLocalRef(x))]))]);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    var result = IrInterpreter.Run(module, "f", [], new() { RecordValues = true });
    Assert.That(result.Outcome, Is.EqualTo(ExecutionOutcome.Aborted));
    Assert.That(result.RecordedValues, Is.Empty);
    Assert.That(result.Diagnostic!.Code, Is.EqualTo("PLY0120"));
    Assert.That(result.Message, Is.EqualTo("'Undef' value '%x' was evaluated"));
    Assert.That(result.At, Is.EqualTo(new InstructionRef("f", null)));
  }

  /// <summary>The caller's mistakes are exceptions, not outcomes.</summary>
  [Test]
  public void TheCallersMistakes_Throw() {
    var module = Import(Pick);
    Assert.Throws<ArgumentNullException>(() => IrInterpreter.Run(null!, "Pick", []));
    Assert.Throws<ArgumentNullException>(() => IrInterpreter.Run(module, null!, []));
    Assert.That(Assert.Throws<ArgumentException>(() => IrInterpreter.Run(module, "Nope", []))!.Message, Does.Contain("no function 'Nope'"));
  }

  /// <summary>Test design 16: the same run twice gives the same outcome, value, steps, trace and probes.</summary>
  [Test]
  public void ARun_IsDeterministic() {
    var module = Import("public static int F(int n) { int[] a = new int[n]; for (int i = 0; i < n; i++) a[i] = i * i; int s = 0; foreach (var v in a) s ^= v; return s; }");
    var first = IrInterpreter.Run(module, "F", [I32(50)]);
    var second = IrInterpreter.Run(module, "F", [I32(50)]);
    Assert.That((second.Outcome, second.Value, second.Steps, second.Allocations, second.MaxFrameDepth), Is.EqualTo((first.Outcome, first.Value, first.Steps, first.Allocations, first.MaxFrameDepth)));
    Assert.That(second.Trace, Is.EqualTo(first.Trace));
  }

  /// <summary>Test design 17: under RecordValues every value each local took, in order, parameters included.</summary>
  [Test]
  public void RecordValues_RecordsEveryValueOfEachLocal() {
    var module = Import("public static int F(int n) { int s = 0; for (int i = 0; i < n; i++) s += i; return s; }");
    var result = IrInterpreter.Run(module, "F", [I32(3)], new() { RecordValues = true });
    var recorded = result.RecordedValues!.ToDictionary(pair => pair.Key.Name, pair => pair.Value.Select(value => (int)value.Number).ToArray());
    Assert.That(recorded["n"], Is.EqualTo(new[] { 3 }));
    Assert.That(recorded["s"], Is.EqualTo(new[] { 0, 0, 1, 3 }));
    Assert.That(recorded["i"].Take(4), Is.EqualTo(new[] { 0, 1, 2, 3 }));
  }

  /// <summary>A module with globals is loaded in the run: its initialisers and its initializer run first.</summary>
  [Test]
  public void AModuleWithGlobals_IsInitialisedInTheRun() {
    var module = Import("static int x = G(4); static int[] t = new int[2]; private static int G(int a) => a + a; public static int F() => x + t.Length;");
    var result = IrInterpreter.Run(module, "F", []);
    Assert.That(result.Value, Is.EqualTo(I32(10)));
    Assert.That(result.Allocations, Is.EqualTo(1));
  }

  /// <summary>The instance API still initialises a module with globals once and runs its initializer, as before.</summary>
  [Test]
  public void Instantiate_StillInitialisesTheModule() {
    var instance = IrInterpreter.Instantiate(Import("static int x = 3; public static int F() => ++x;"));
    Assert.That((instance.Call("F"), instance.Call("F")), Is.EqualTo((4, 5)));
  }

  /// <summary>The options refuse what is no budget.</summary>
  [Test]
  public void TheOptions_RefuseWhatIsNoBudget() {
    Assert.Throws<ArgumentOutOfRangeException>(() => _ = new InterpreterOptions { MaxSteps = -1 });
    Assert.Throws<ArgumentOutOfRangeException>(() => _ = new InterpreterOptions { MaxCallDepth = 0 });
    var least = new InterpreterOptions { MaxSteps = 0, MaxCallDepth = 1 };
    Assert.That((least.MaxSteps, least.MaxCallDepth), Is.EqualTo((0L, 1)));
    Assert.That((InterpreterOptions.Default.MaxSteps, InterpreterOptions.Default.MaxCallDepth), Is.EqualTo((10_000_000L, 10_000)));
  }

  /// <summary>A call depth of one runs a function that calls nothing and aborts one that calls.</summary>
  [Test]
  public void ACallDepthOfOne_AllowsNoCall() {
    var module = Import("public static int F() => 1; public static int G() => F();");
    var options = new InterpreterOptions { MaxCallDepth = 1 };
    Assert.That(IrInterpreter.Run(module, "F", [], options).Outcome, Is.EqualTo(ExecutionOutcome.Returned));
    Assert.That(IrInterpreter.Run(module, "G", [], options).Diagnostic!.Message, Is.EqualTo("call depth 1 exceeded"));
  }

  /// <summary>The exception API keeps its budget of depth and its message.</summary>
  [Test]
  public void TheExceptionApi_KeepsItsDepthBudget()
    => Assert.That(Assert.Throws<IrBudgetException>(() => IrInterpreter.Call(Import(Depth), "D", [200], 1_000_000))!.Message,
      Is.EqualTo($"The call nests calls deeper than {IrInterpreter.BudgetDepth}."));

}
