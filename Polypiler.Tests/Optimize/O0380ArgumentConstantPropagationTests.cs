using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Core.Optimize.Passes;

namespace Polypiler.Tests.Optimize;

/// <summary>
/// O0380 on hand-built IR (#1151): the scenarios of <c>opt-ipa-constarg.feature</c> and the rows of the
/// test design - where a parameter becomes the literal every call passes, every case where it must not,
/// the boundaries of one and of no call site and of floats equal by value but not by bits - each with the
/// module computing the same before and after.
/// </summary>
[TestFixture]
public sealed class O0380ArgumentConstantPropagationTests {

  private static readonly IrLocal _mode = new("mode", IrType.I32);
  private static readonly IrLocal _x = new("x", IrType.I32);
  private static readonly IrLocal _a = new("a", IrType.I32);

  private static IrConst Int(long value) => new(IrType.I32, value);

  private static IrExpr Ref(IrLocal local) => new IrLocalRef(local);

  private static IrBlock Block(params IrStmt[] statements) => new(statements);

  private static IrCall Call(string function, params IrExpr[] arguments) => new(function, IrType.I32, arguments);

  private static IrExpr Sum(params IrExpr[] terms) => terms.Aggregate((left, right) => new IrBinary(IrBinaryOp.Add, left, right));

  /// <summary><c>Draw(mode, x)</c>: <c>x</c> in text mode 1, <c>-x</c> otherwise.</summary>
  private static IrFunction Draw(bool isPublic = false)
    => new("Draw", isPublic, IrType.I32, [_mode, _x], [], Block(
      new IrIf(new IrBinary(IrBinaryOp.Equal, Ref(_mode), Int(1)), Block(new IrReturn(Ref(_x))), Block(new IrReturn(new IrUnary(IrUnaryOp.Negate, Ref(_x)))))));

  /// <summary>The public <c>Entry(a)</c>, which returns the sum of the calls; <c>a</c> is there to be passed on.</summary>
  private static IrFunction Entry(params IrExpr[] calls) => new("Main", true, IrType.I32, [_a], [], Block(new IrReturn(Sum(calls))));

  private static IrModule DrawModule(params (IrExpr Mode, int X)[] calls)
    => new("M", [Draw(), Entry([.. calls.Select(call => (IrExpr)Call("Draw", call.Mode, Int(call.X)))])]);

  private static (IrModule Module, List<string> Applied, List<string> Notes) Run(IrModule module, Func<IrFunction, bool>? scope = null) {
    var applied = new List<string>();
    var notes = new List<string>();
    var result = new O0380ArgumentConstantPropagation().Run(module, scope ?? (_ => true), (function, position) => applied.Add($"{function}@{position}"), notes.Add);
    Assert.That(IrVerifier.Verify(result), Is.Empty);
    return (result, applied, notes);
  }

  private static IrFunction Function(IrModule module, string name) => module.Functions.Single(function => function.Name == name);

  private static bool Reads(IrModule module, string function, IrLocal local)
    => IrTree.Nodes(Function(module, function).Body).OfType<IrLocalRef>().Any(reference => reference.Local == local);

  private static readonly int[] _inputs = [0, 1, -1, 7, int.MaxValue, int.MinValue];

  private static void SameOutcomes(IrModule before, IrModule after, string function = "Main") {
    var random = new Random(380);
    foreach (var input in _inputs.Concat(Enumerable.Range(0, 16).Select(_ => random.Next(int.MinValue, int.MaxValue))))
      Assert.That(Outcome(after, function, input), Is.EqualTo(Outcome(before, function, input)), $"{function}({input})");
  }

  private static string Outcome(IrModule module, string function, int input) {
    try {
      return Convert.ToString(IrInterpreter.Call(module, function, [input]), System.Globalization.CultureInfo.InvariantCulture)!;
    } catch (IrException exception) {
      return "throws " + exception.Class.Name;
    }
  }

  // ---------------------------------------------------------------- the scenarios

  [Test]
  public void AParameterThatIsOneAtEveryCallSite_ReadsAsOneInTheCallee() {
    var module = DrawModule((Int(1), 10), (Int(1), 20));
    var (result, applied, notes) = Run(module);
    var draw = Function(result, "Draw");
    Assert.That(IrTree.Nodes(draw.Body).OfType<IrBinary>().First(), Is.EqualTo(new IrBinary(IrBinaryOp.Equal, Int(1), Int(1))));
    Assert.That(draw.Parameters, Is.EqualTo(new[] { _mode, _x }), "the signature is unchanged");
    Assert.That(Function(result, "Main"), Is.SameAs(Function(module, "Main")), "the calls are unchanged");
    Assert.That(applied, Is.EqualTo(new[] { "Draw@0" }));
    Assert.That(notes, Does.Contain("O0380 applied: @Draw: parameter %mode = 1 at all 2 call sites [proof: structural]"));
    Assert.That(IrInterpreter.Call(result, "Main", [0]), Is.EqualTo(30));
    SameOutcomes(module, result);
  }

  [Test]
  public void CallSitesThatDisagree_LeaveTheParameterAlone() {
    var module = DrawModule((Int(1), 10), (Int(1), 20), (Int(2), 30));
    var (result, applied, notes) = Run(module);
    Assert.That(result, Is.SameAs(module));
    Assert.That(applied, Is.Empty);
    Assert.That(notes, Does.Contain("O0380 not applied: @Draw: %mode: arguments differ (1 at @Main, 2 at @Main)"));
  }

  [Test]
  public void AnExportedFunction_IsNeverSpecialised() {
    var module = DrawModule((Int(1), 10), (Int(1), 20));
    module = module with { Functions = [Draw(isPublic: true), module.Functions[1]] };
    var (result, _, notes) = Run(module);
    Assert.That(result, Is.SameAs(module));
    Assert.That(notes, Does.Contain("O0380 not applied: @Draw: %mode: exported"));
  }

  [Test]
  public void AFunctionWhoseAddressIsTaken_IsNeverSpecialised() {
    var type = IrType.Function(IrType.I32, IrType.I32, IrType.I32);
    var table = new IrGlobal("table", type, null);
    var main = new IrFunction("Main", true, IrType.I32, [_a], [], Block(
      new IrGlobalAssign(table, new IrFunctionRef(type, "Draw")),
      new IrReturn(Sum(Call("Draw", Int(1), Int(10)), Call("Draw", Int(1), Int(20))))));
    var module = new IrModule("M", [Draw(), main]) { Globals = [table] };
    var (result, _, notes) = Run(module);
    Assert.That(result, Is.SameAs(module));
    Assert.That(notes, Does.Contain("O0380 not applied: @Draw: %mode: address taken"));
  }

  /// <summary>Zeros of different sign, and two NaNs of different payloads, are different constants: <c>1 / d</c> tells the zeros apart.</summary>
  [TestCase(0x0000000000000000L, unchecked((long)0x8000000000000000UL), "0", "-0")]
  [TestCase(0x7FF8000000000000L, 0x7FF8000000000001L, "NaN", "NaN")]
  public void FloatsEqualByValueButNotByBits_AreDifferentConstants(long first, long second, string firstSpelled, string secondSpelled) {
    var d = new IrLocal("d", IrType.F64);
    var inv = new IrFunction("inv", false, IrType.F64, [d], [], Block(new IrReturn(new IrBinary(IrBinaryOp.Divide, new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, 1.0)), Ref(d)))));
    var main = new IrFunction("Main", true, IrType.F64, [], [], Block(
      new IrReturn(new IrBinary(IrBinaryOp.Add, new IrCall("inv", IrType.F64, [new IrConst(IrType.F64, first)]), new IrCall("inv", IrType.F64, [new IrConst(IrType.F64, second)])))));
    var module = new IrModule("M", [inv, main]);
    var (result, _, notes) = Run(module);
    Assert.That(result, Is.SameAs(module));
    Assert.That(notes, Does.Contain($"O0380 not applied: @inv: %d: arguments differ ({firstSpelled} at @Main, {secondSpelled} at @Main)"));
  }

  [Test]
  public void TheSwitch_DisablesTheRule() {
    var module = DrawModule((Int(1), 10), (Int(1), 20));
    var disabled = IrPassPipeline.Default.Run(module, new OptimizerOptions { Verify = true, Disabled = new HashSet<string> { "O0380" } });
    Assert.That(disabled.Applications.Select(application => application.Id), Has.None.EqualTo("O0380"));
    Assert.That(disabled.Notes, Has.None.Contains("O0380"));
    var none = IrPassPipeline.Default.Run(module, new OptimizerOptions { Level = OptimizationLevel.None });
    Assert.That(none.Module, Is.SameAs(module));
    Assert.That(none.Applications, Is.Empty);
    var enabled = IrPassPipeline.Default.Run(module, new OptimizerOptions { Verify = true });
    Assert.That(enabled.Applications.Select(application => application.Id), Does.Contain("O0380"));
    Assert.That(enabled.Notes, Does.Contain("O0380 applied: @Draw: parameter %mode = 1 at all 2 call sites [proof: structural]"));
    SameOutcomes(module, enabled.Module);
  }

  // ---------------------------------------------------------------- the test design

  /// <summary>Row 2 and 6: a literal passed on reaches the next function, and the one after it, in one run.</summary>
  [Test]
  public void AConstantPassedOn_IsPropagatedToTheFixedPoint() {
    var p = new IrLocal("p", IrType.I32);
    var q = new IrLocal("q", IrType.I32);
    var r = new IrLocal("r", IrType.I32);
    var a = new IrFunction("A", false, IrType.I32, [p], [], Block(new IrReturn(Sum(Call("B", Ref(p)), Ref(p)))));
    var b = new IrFunction("B", false, IrType.I32, [q], [], Block(new IrReturn(Sum(Call("C", Ref(q)), Ref(q)))));
    var c = new IrFunction("C", false, IrType.I32, [r], [], Block(new IrReturn(new IrBinary(IrBinaryOp.Multiply, Ref(r), Ref(r)))));
    var module = new IrModule("M", [Entry(Call("A", Int(5))), a, b, c]);
    var (result, applied, _) = Run(module);
    Assert.That(Reads(result, "A", p) || Reads(result, "B", q) || Reads(result, "C", r), Is.False);
    Assert.That(applied, Is.EquivalentTo(new[] { "A@0", "B@0", "C@0" }));
    Assert.That(Function(result, "C").Body.Statements[0], Is.EqualTo(new IrReturn(new IrBinary(IrBinaryOp.Multiply, Int(5), Int(5)))));
    SameOutcomes(module, result);
  }

  /// <summary>Row 3: each kind of scalar literal - a truth value, a float, a null and a reference to a function - is substituted.</summary>
  [Test]
  public void EachKindOfScalar_IsReplacedByItsLiteral() {
    var unary = IrType.Function(IrType.I32, IrType.I32);
    var flag = new IrLocal("flag", IrType.Bool);
    var scale = new IrLocal("scale", IrType.F64);
    var none = new IrLocal("none", unary);
    var code = new IrLocal("code", unary);
    var y = new IrLocal("y", IrType.I32);
    var g = new IrFunction("G", false, IrType.I32, [y], [], Block(new IrReturn(Sum(Ref(y), Int(1)))));
    var f = new IrFunction("F", false, IrType.I32, [flag, scale, none, code], [], Block(
      new IrIf(new IrIsNull(Ref(none)), Block(), Block(new IrReturn(Int(-1)))),
      new IrIf(Ref(flag), Block(new IrReturn(new IrInvoke(IrType.I32, Ref(code), [new IrConvert(IrType.I32, Ref(scale), IrConversion.Saturate)]))), null),
      new IrReturn(Int(0))));
    IrExpr[] literals = [new IrConst(IrType.Bool, 1), new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, 2.5)), new IrNull(unary), new IrFunctionRef(unary, "G")];
    var module = new IrModule("M", [Entry(Call("F", literals), Call("F", literals)), f, g]);
    var (result, _, notes) = Run(module);
    Assert.That(new[] { flag, scale, none, code }.Any(parameter => Reads(result, "F", parameter)), Is.False);
    Assert.That(notes, Is.SupersetOf(new[] {
      "O0380 applied: @F: parameter %flag = true at all 2 call sites [proof: structural]",
      "O0380 applied: @F: parameter %scale = 2.5 at all 2 call sites [proof: structural]",
      "O0380 applied: @F: parameter %none = null at all 2 call sites [proof: structural]",
      "O0380 applied: @F: parameter %code = @G at all 2 call sites [proof: structural]"
    }));
    SameOutcomes(module, result);
  }

  /// <summary>The literals as the opt-report spells them: a false, a 64-bit word above the largest signed one, a negative number, a single-precision float.</summary>
  [TestCase("bool", 0L, "false")]
  [TestCase("u64", -1L, "18446744073709551615")]
  [TestCase("i64", -3L, "-3")]
  [TestCase("f32", 0x3FC00000L, "1.5")]
  public void TheReport_SpellsTheLiteralInItsType(string kind, long value, string spelled) {
    var type = kind switch { "bool" => IrType.Bool, "u64" => IrType.U64, "i64" => IrType.I64, _ => IrType.F32 };
    var v = new IrLocal("v", type);
    var f = new IrFunction("F", false, IrType.I32, [v], [], Block(new IrExprStmt(Ref(v)), new IrReturn(Int(0))));
    var module = new IrModule("M", [Entry(Call("F", new IrConst(type, value))), f]);
    var (_, _, notes) = Run(module);
    Assert.That(notes, Does.Contain($"O0380 applied: @F: parameter %v = {spelled} at all 1 call sites [proof: structural]"));
  }

  /// <summary>Row 4: a recursive call that passes the parameter on at its own position does not count; the counter it passes changed does.</summary>
  [Test]
  public void ARecursivePassThrough_IsIgnoredWhenComparing() {
    var n = new IrLocal("n", IrType.I32);
    var f = new IrFunction("f", false, IrType.I32, [n, _mode], [], Block(
      new IrIf(new IrBinary(IrBinaryOp.LessOrEqual, Ref(n), Int(0)), Block(new IrReturn(Ref(_mode))), null),
      new IrReturn(Sum(Call("f", new IrBinary(IrBinaryOp.Subtract, Ref(n), Int(1)), Ref(_mode)), Int(1)))));
    var module = new IrModule("M", [Entry(Call("f", Int(3), Int(7))), f]);
    var (result, _, notes) = Run(module);
    Assert.That(Reads(result, "f", _mode), Is.False);
    Assert.That(Reads(result, "f", n), Is.True);
    Assert.That(notes, Does.Contain("O0380 not applied: @f: %n: argument at @f is not a literal"));
    Assert.That(IrInterpreter.Call(result, "Main", [0]), Is.EqualTo(10));
    SameOutcomes(module, result);
  }

  /// <summary>A recursive call from a lambda in the function is not the function passing its parameter on: the lambda's variable may be another.</summary>
  [Test]
  public void ACallFromALambdaOfTheFunction_IsNoPassThrough() {
    var type = IrType.Function(IrType.I32);
    var lambda = new IrLambda(type, [_mode], [], [], Block(new IrReturn(Call("f", Ref(_mode)))));
    var f = new IrFunction("f", false, IrType.I32, [_mode], [], Block(
      new IrIf(new IrBinary(IrBinaryOp.Equal, Ref(_mode), Int(1)), Block(new IrReturn(Int(1))), null),
      new IrReturn(new IrInvoke(IrType.I32, lambda, []))));
    var module = new IrModule("M", [Entry(Call("f", Int(1))), f]);
    var (result, _, notes) = Run(module);
    Assert.That(result, Is.SameAs(module));
    Assert.That(notes, Does.Contain("O0380 not applied: @f: %mode: argument at @f is not a literal"));
  }

  /// <summary>Row 5 and 10: of two parameters only the constant one is replaced; the other differs between the calls.</summary>
  [Test]
  public void OfSeveralParameters_OnlyTheConstantOneIsReplaced() {
    var (result, _, notes) = Run(DrawModule((Int(1), 10), (Int(1), 20)));
    Assert.That(Reads(result, "Draw", _mode), Is.False);
    Assert.That(Reads(result, "Draw", _x), Is.True);
    Assert.That(notes, Does.Contain("O0380 not applied: @Draw: %x: arguments differ (10 at @Main, 20 at @Main)"));
  }

  /// <summary>Row 7: exactly one call site is enough.</summary>
  [Test]
  public void ExactlyOneCallSite_IsReplaced() {
    var module = DrawModule((Int(7), 3));
    var (result, _, notes) = Run(module);
    Assert.That(Reads(result, "Draw", _mode) || Reads(result, "Draw", _x), Is.False);
    Assert.That(notes, Does.Contain("O0380 applied: @Draw: parameter %mode = 7 at all 1 call sites [proof: structural]"));
    SameOutcomes(module, result);
  }

  /// <summary>Row 8: an internal function nothing calls has no call site to take a literal from.</summary>
  [Test]
  public void NoCallSite_IsReported() {
    var module = new IrModule("M", [Draw(), Entry(Int(0))]);
    var (result, _, notes) = Run(module);
    Assert.That(result, Is.SameAs(module));
    Assert.That(notes, Does.Contain("O0380 not applied: @Draw: %mode: no call site"));
    Assert.That(notes, Has.None.Contains("@Main"), "a public function nothing in the module calls is no candidate");
  }

  /// <summary>Row 11: a variable at one call site, a literal at the other.</summary>
  [Test]
  public void ANonLiteralArgument_IsReported() {
    var module = DrawModule((Ref(_a), 10), (Int(1), 20));
    var (result, _, notes) = Run(module);
    Assert.That(Reads(result, "Draw", _mode), Is.True);
    Assert.That(notes, Does.Contain("O0380 not applied: @Draw: %mode: argument at @Main is not a literal"));
  }

  /// <summary>A parameter whose every argument is a variable is no candidate and is not reported.</summary>
  [Test]
  public void NoLiteralAtAll_IsNotReported() {
    var module = DrawModule((Ref(_a), 10));
    var (_, _, notes) = Run(module);
    Assert.That(notes, Has.None.Contains("%mode"));
  }

  /// <summary>Row 14: <c>f(1)</c> calls <c>g(p)</c>, which calls <c>f(2)</c>.</summary>
  [Test]
  public void MutualRecursionWithAnotherLiteralOnTheBackEdge_IsRefused() {
    var p = new IrLocal("p", IrType.I32);
    var q = new IrLocal("q", IrType.I32);
    var f = new IrFunction("f", false, IrType.I32, [p], [], Block(
      new IrIf(new IrBinary(IrBinaryOp.Equal, Ref(p), Int(1)), Block(new IrReturn(Call("g", Ref(p)))), null),
      new IrReturn(Ref(p))));
    var g = new IrFunction("g", false, IrType.I32, [q], [], Block(new IrReturn(Sum(Call("f", Int(2)), Ref(q)))));
    var module = new IrModule("M", [Entry(Call("f", Int(1))), f, g]);
    var (result, _, notes) = Run(module);
    Assert.That(result, Is.SameAs(module));
    Assert.That(notes, Does.Contain("O0380 not applied: @f: %p: arguments differ (1 at @Main, 2 at @g)"));
    SameOutcomes(module, result);
  }

  /// <summary>A parameter the function writes does not keep the literal it arrived with.</summary>
  [Test]
  public void AParameterTheFunctionWrites_IsRefused() {
    var p = new IrLocal("p", IrType.I32);
    var f = new IrFunction("F", false, IrType.I32, [p], [], Block(new IrAssign(p, Sum(Ref(p), Int(1))), new IrReturn(Ref(p))));
    var module = new IrModule("M", [Entry(Call("F", Int(1))), f]);
    var (result, _, notes) = Run(module);
    Assert.That(result, Is.SameAs(module));
    Assert.That(notes, Does.Contain("O0380 not applied: @F: %p: written in the function"));
  }

  /// <summary>A lambda that writes the parameter, and an address taken of it, make it a variable too.</summary>
  [TestCase(true)]
  [TestCase(false)]
  public void AParameterWrittenByALambdaOrThroughItsAddress_IsRefused(bool lambda) {
    var p = new IrLocal("p", IrType.I32);
    IrStmt writer = lambda
      ? new IrExprStmt(new IrInvoke(IrType.Void, new IrLambda(IrType.Function(IrType.Void), [p], [], [], Block(new IrAssign(p, Int(2)))), []))
      : new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.AddressOf, IrType.Pointer(IrType.I32), [Ref(p)]));
    var f = new IrFunction("F", false, IrType.I32, [p], [], Block(writer, new IrReturn(Ref(p))));
    var module = new IrModule("M", [Entry(Call("F", Int(1))), f]);
    var notes = new List<string>();
    var result = new O0380ArgumentConstantPropagation().Run(module, _ => true, (_, _) => { }, notes.Add);
    Assert.That(result, Is.SameAs(module));
    Assert.That(notes, Does.Contain("O0380 not applied: @F: %p: written in the function"));
  }

  /// <summary>A read in the body of a lambda stays a read of the variable it captures; with no other read nothing changes.</summary>
  [Test]
  public void AReadOnlyInALambda_IsLeftAlone() {
    var p = new IrLocal("p", IrType.I32);
    var f = new IrFunction("F", false, IrType.I32, [p], [], Block(
      new IrReturn(new IrInvoke(IrType.I32, new IrLambda(IrType.Function(IrType.I32), [p], [], [], Block(new IrReturn(Ref(p)))), []))));
    var module = new IrModule("M", [Entry(Call("F", Int(4))), f]);
    var (result, _, notes) = Run(module);
    Assert.That(result, Is.SameAs(module));
    Assert.That(notes, Is.Empty);
  }

  /// <summary>A call in the initializer of a global is a call site as well.</summary>
  [TestCase(3, true)]
  [TestCase(4, false)]
  public void ACallInTheInitializerOfAGlobal_IsACallSite(int initial, bool replaced) {
    var p = new IrLocal("p", IrType.I32);
    var f = new IrFunction("F", false, IrType.I32, [p], [], Block(new IrReturn(new IrBinary(IrBinaryOp.Multiply, Ref(p), Int(2)))));
    var seed = new IrGlobal("seed", IrType.I32, Call("F", Int(initial)));
    var main = new IrFunction("Main", true, IrType.I32, [_a], [], Block(new IrReturn(Sum(Call("F", Int(3)), new IrGlobalRef(seed)))));
    var module = new IrModule("M", [main, f]) { Globals = [seed] };
    var (result, _, notes) = Run(module);
    Assert.That(Reads(result, "F", p), Is.EqualTo(!replaced));
    if (!replaced)
      Assert.That(notes, Does.Contain("O0380 not applied: @F: %p: arguments differ (3 at @Main, 4 at a global initializer)"));

    SameOutcomes(module, result);
  }

  /// <summary>A function the pipeline does not look at keeps its parameters, whatever its calls pass.</summary>
  [Test]
  public void AFunctionOutOfScope_IsLeftAlone() {
    var module = DrawModule((Int(1), 10));
    var (result, _, _) = Run(module, function => function.Name != "Draw");
    Assert.That(result, Is.SameAs(module));
  }

  /// <summary>A function only it calls itself, passing its parameter on, has no literal to take.</summary>
  [Test]
  public void AFunctionOnlyItselfCalls_HasNoLiteral() {
    var p = new IrLocal("p", IrType.I32);
    var f = new IrFunction("F", false, IrType.I32, [p], [], Block(new IrReturn(Call("F", Ref(p)))));
    var module = new IrModule("M", [Entry(Int(0)), f]);
    var (result, _, notes) = Run(module);
    Assert.That(result, Is.SameAs(module));
    Assert.That(notes, Is.Empty);
  }

  /// <summary>A third call added by inlining is seen: the pass counts the calls of the module as it is when it runs.</summary>
  [Test]
  public void ACallAddedLater_IsCounted() {
    var module = DrawModule((Int(1), 10));
    var grown = module with { Functions = [module.Functions[0], Entry(Call("Draw", Int(1), Int(10)), Call("Draw", Int(2), Int(3)))] };
    Assert.That(Run(module).Module, Is.Not.SameAs(module));
    Assert.That(Run(grown).Module, Is.SameAs(grown));
  }

  [Test]
  public void ArgumentsAreChecked() {
    var pass = new O0380ArgumentConstantPropagation();
    var module = DrawModule((Int(1), 10));
    Assert.That(pass.Id, Is.EqualTo("O0380"));
    Assert.That(pass.Name, Is.EqualTo("interprocedural constant propagation"));
    Assert.Throws<ArgumentNullException>(() => pass.Run(null!, _ => true, (_, _) => { }));
    Assert.Throws<ArgumentNullException>(() => pass.Run(module, null!, (_, _) => { }));
    Assert.Throws<ArgumentNullException>(() => pass.Run(module, _ => true, null!));
    Assert.Throws<ArgumentNullException>(() => pass.Run(module, _ => true, (_, _) => { }, null!));
    Assert.That(pass.Run(module, _ => true, (_, _) => { }), Is.Not.SameAs(module));
  }

}
