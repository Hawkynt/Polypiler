using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Async;

/// <summary>
/// Async functions in the IR (#1330) on modules built by hand and on the fixture: the type, the rules of
/// the verifier, the rules of isolation and of faults, the byte format, what the interpreter does and
/// what the optimizer keeps.
/// </summary>
[TestFixture]
public sealed class IrAsyncTests {

  private static readonly IrType Ints = IrType.Async(IrType.I32);

  private static IrConst I(long value) => new(IrType.I32, value);

  private static IrBlock B(params IrStmt[] statements) => new(statements);

  private static IReadOnlyList<string> Problems(params IrFunction[] functions) => [.. IrVerifier.Verify(new IrModule("M", functions)).Select(diagnostic => diagnostic.Message)];

  private static IrFunction Async(string name, IrType type, params IrStmt[] statements) => new(name, true, type, [], [], B(statements));

  /// <summary>An async function <c>F</c> that returns 1.</summary>
  private static IrFunction F => Async("F", Ints, new IrReturn(I(1)));

  private static IrCall CallF => new("F", Ints, []);

  [Test]
  public void Type_NamesItsResultAndItsFlag() {
    Assert.That(Ints.ToString(), Is.EqualTo("async<i32>"));
    Assert.That(IrType.Async(IrType.Void).ToString(), Is.EqualTo("async<void>"));
    Assert.That(IrType.Async(IrType.I32, true).ToString(), Is.EqualTo("async<i32, value task>"));
    Assert.That(Ints, Is.Not.EqualTo(IrType.Async(IrType.I32, true)));
    Assert.That(Ints.IsAsync && !Ints.IsValueTask && IrType.Async(IrType.I32, true).IsValueTask, Is.True);
    Assert.That(Ints.IsStorable || IrType.I32.IsAsync || IrType.I32.IsValueTask, Is.False);
  }

  [TestCase(IrTypeKind.Void, true), TestCase(IrTypeKind.I32, true), TestCase(IrTypeKind.String, true)]
  public void Result_IsNoneOrAStorableValue(IrTypeKind kind, bool expected) {
    var type = kind == IrTypeKind.Void ? IrType.Void : kind == IrTypeKind.String ? IrType.String : IrType.I32;
    Assert.That(IrAsyncFunctions.IsResult(type), Is.EqualTo(expected));
  }

  [Test]
  public void Result_IsNoViewPointerSequenceOrMemory() {
    Assert.That(IrAsyncFunctions.IsResult(IrType.Span(IrType.U8)), Is.False);
    Assert.That(IrAsyncFunctions.IsResult(IrType.Pointer(IrType.U8)), Is.False);
    Assert.That(IrAsyncFunctions.IsResult(IrType.Sequence(IrType.U8, true)), Is.False);
    Assert.That(IrAsyncFunctions.IsResult(IrType.Memory(IrType.U8)), Is.False);
    Assert.That(IrAsyncFunctions.IsResult(Ints), Is.False);
  }

  [Test]
  public void Signature_OfEveryOperation() {
    Assert.That(IrAsyncFunctions.Signature(IrAsyncOp.Completed, []).Type, Is.EqualTo(IrType.Async(IrType.Void)));
    Assert.That(IrAsyncFunctions.Signature(IrAsyncOp.Completed, [IrType.I32]).Problem, Is.EqualTo("Completed takes no operand"));
    Assert.That(IrAsyncFunctions.Signature(IrAsyncOp.FromResult, [IrType.I32]).Type, Is.EqualTo(Ints));
    Assert.That(IrAsyncFunctions.Signature(IrAsyncOp.FromResult, []).Problem, Is.EqualTo("FromResult takes one value"));
    Assert.That(IrAsyncFunctions.Signature(IrAsyncOp.FromResult, [IrType.Void]).Problem, Is.EqualTo("FromResult takes one value"));
    Assert.That(IrAsyncFunctions.Signature(IrAsyncOp.WhenAll, [Ints, Ints]).Type, Is.EqualTo(IrType.Async(IrType.Array(IrType.I32))));
    Assert.That(IrAsyncFunctions.Signature(IrAsyncOp.WhenAll, [], IrType.I32).Type, Is.EqualTo(IrType.Async(IrType.Array(IrType.I32))));
    Assert.That(IrAsyncFunctions.Signature(IrAsyncOp.WhenAll, []).Type, Is.EqualTo(IrType.Async(IrType.Void)));
    Assert.That(IrAsyncFunctions.Signature(IrAsyncOp.WhenAll, [IrType.Async(IrType.Void)]).Type, Is.EqualTo(IrType.Async(IrType.Void)));
    Assert.That(IrAsyncFunctions.Signature(IrAsyncOp.WhenAll, [IrType.I32]).Problem, Is.EqualTo("WhenAll takes async values"));
    Assert.That(IrAsyncFunctions.Signature(IrAsyncOp.WhenAll, [IrType.Async(IrType.I32, true)]).Problem, Is.EqualTo("WhenAll takes async values"));
    Assert.That(IrAsyncFunctions.Signature(IrAsyncOp.WhenAll, [Ints, IrType.Async(IrType.F64)]).Problem, Is.EqualTo("WhenAll takes async values of one result type"));
    Assert.That(IrAsyncFunctions.Signature(IrAsyncOp.WhenAll, [IrType.Async(IrType.Array(IrType.Span(IrType.U8)))]).Problem, Does.StartWith("WhenAll of results"));
  }

  [Test]
  public void Fixture_IsWellTyped() => Assert.That(IrVerifier.Verify(AsyncFixtures.Imported()).Select(diagnostic => diagnostic.Message), Is.Empty);

  [Test]
  public void AwaitedCall_IsWellTyped() {
    var caller = Async("G", Ints, new IrReturn(new IrAwait(CallF)));
    Assert.That(Problems(F, caller), Is.Empty);
    Assert.That(new IrAwait(CallF).Type, Is.EqualTo(IrType.I32));
    Assert.That(new IrAwait(I(1)).Type, Is.EqualTo(IrType.Void));
  }

  /// <summary>Test design 12: an await outside an async function, in a lambda, or of something else than a call or an async value.</summary>
  [Test]
  public void Await_OnlyInAnAsyncFunctionOfACall() {
    var plain = new IrFunction("G", true, IrType.I32, [], [], B(new IrReturn(new IrAwait(CallF))));
    Assert.That(Problems(F, plain), Does.Contain("ill-typed IR in 'G': an await outside an async function"));

    var lambda = new IrLambda(IrType.Function(IrType.I32), [], [], [], B(new IrReturn(new IrAwait(CallF))));
    var holder = new IrLocal("h", lambda.Type);
    var inLambda = new IrFunction("G", true, Ints, [], [holder], B(new IrDeclare(holder, lambda), new IrReturn(I(1))));
    Assert.That(Problems(F, inLambda), Does.Contain("ill-typed IR in 'G': an await inside a lambda"));

    var x = new IrLocal("x", IrType.I32);
    var ofValue = new IrFunction("G", true, Ints, [x], [], B(new IrReturn(new IrAwait(new IrLocalRef(x)))));
    Assert.That(Problems(ofValue), Has.Some.Contains("an await of a IrLocalRef"));
  }

  /// <summary>Test design 12: an async value anywhere but where it is awaited.</summary>
  [Test]
  public void AsyncValue_NotAwaited_IsIllTyped() {
    var discarded = Async("G", Ints, new IrExprStmt(CallF), new IrReturn(I(1)));
    Assert.That(Problems(F, discarded), Does.Contain("ill-typed IR in 'G': " + IrAsyncFunctions.NotAwaited));

    var held = new IrLocal("t", Ints);
    var stored = Async("G", Ints, new IrReturn(I(1))) with { Locals = [held] };
    Assert.That(Problems(F, stored), Has.Some.Contains("local 't' has the type async<i32>"));

    var notAwaitedAll = Async("G", Ints, new IrExprStmt(new IrAsync(IrAsyncOp.WhenAll, IrType.Async(IrType.Array(IrType.I32)), [CallF])), new IrReturn(I(1)));
    Assert.That(Problems(F, notAwaitedAll), Does.Contain("ill-typed IR in 'G': " + IrAsyncFunctions.NotAwaited));
  }

  [Test]
  public void AsyncOperation_FitsItsSignature() {
    var wrong = Async("G", Ints, new IrReturn(new IrAwait(new IrAsync(IrAsyncOp.FromResult, IrType.Async(IrType.F64), [I(1)]))));
    Assert.That(Problems(wrong), Has.Some.Contains("FromResult has the type async<f64>, expected async<i32>"));
    var none = Async("G", Ints, new IrReturn(new IrAwait(new IrAsync(IrAsyncOp.FromResult, Ints, []))));
    Assert.That(Problems(none), Has.Some.Contains("FromResult takes one value"));
    var fromValue = Async("G", IrType.Async(IrType.Void), new IrExprStmt(new IrAwait(new IrAsync(IrAsyncOp.WhenAll, IrType.Async(IrType.Void), [new IrAwait(new IrAsync(IrAsyncOp.Completed, IrType.Async(IrType.Void), []))]))));
    Assert.That(Problems(fromValue), Has.Some.Contains("WhenAll takes async values"));
    var ofCompleted = Async("G", IrType.Async(IrType.Void), new IrExprStmt(new IrAwait(new IrAsync(IrAsyncOp.WhenAll, IrType.Async(IrType.Void), [new IrAsync(IrAsyncOp.Completed, IrType.Async(IrType.Void), [])]))));
    Assert.That(Problems(ofCompleted), Has.Some.Contains("WhenAll of an async value that is not the call of an async function"));
  }

  [Test]
  public void AsyncOperation_OfATypeThatIsNoAsyncType_IsIllTyped() {
    var wrong = Async("G", IrType.Async(IrType.Void), new IrExprStmt(new IrAwait(new IrAsync(IrAsyncOp.Completed, IrType.I32, []))));
    Assert.That(Problems(wrong), Has.Some.Contains("Completed has the type i32, expected async<void>"));
  }

  [Test]
  public void Return_OfAnAsyncFunction_IsOfItsResult() {
    Assert.That(Problems(Async("G", Ints, new IrReturn(null))), Has.Some.Contains("a return without a value in a function that returns one"));
    Assert.That(Problems(Async("G", Ints, new IrReturn(new IrStrConst("x")))), Has.Some.Contains("the value returned has the type str, expected i32"));
    Assert.That(Problems(Async("G", IrType.Async(IrType.Void), new IrReturn(null))), Is.Empty);
    Assert.That(Problems(Async("G", IrType.Async(IrType.Span(IrType.U8)), new IrReturn(null))), Has.Some.Contains("the return type async<span<u8>> is not a type of the IR"));
  }

  /// <summary>Test design 11: a WhenAll of a function that writes a global, an array or calls one that does, refused by the verifier.</summary>
  [Test]
  public void WhenAll_OfAFunctionThatWritesMoreThanItsLocals_IsIllTyped() {
    var array = new IrLocal("a", IrType.Array(IrType.I32));
    var writes = new IrFunction("W", false, Ints, [array], [], B(new IrStore(new IrLocalRef(array), I(0), I(1)), new IrReturn(I(1))));
    var through = new IrFunction("T", false, Ints, [array], [], B(new IrReturn(new IrAwait(new IrCall("W", Ints, [new IrLocalRef(array)])))));
    var all = new IrFunction("G", true, IrType.Async(IrType.Void), [array], [],
      B(new IrExprStmt(new IrAwait(new IrAsync(IrAsyncOp.WhenAll, IrType.Async(IrType.Array(IrType.I32)), [new IrCall("T", Ints, [new IrLocalRef(array)]), CallF])))));
    Assert.That(Problems(F, writes, through, all), Is.EqualTo(new[] { "ill-typed IR in 'G': WhenAll of 'T', which writes more than its own locals, whose order depends on timing" }));
    Assert.That(IrAsyncFunctions.Isolated(new IrModule("M", [F, writes, through, all])), Is.EquivalentTo(new[] { "F" }));
  }

  [Test]
  public void Isolated_KeepsLocalsAndStructsOfLocals() {
    var x = new IrLocal("x", IrType.I32);
    var stepped = new IrFunction("S", false, Ints, [x], [], B(new IrExprStmt(new IrStep(new IrLocalRef(x), true, false, false)), new IrReturn(new IrLocalRef(x))));
    var copied = new IrFunction("C", false, IrType.Void, [], [], B(new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayCopy, IrType.Void, []))));
    var faulty = new IrFunction("E", false, Ints, [], [], B(new IrThrow(new IrNew(IrType.Object(IrExceptionClass.Exception), IrExceptionClass.Exception.Constructors[0], []))));
    var point = new IrStructType("P");
    point.Define([new IrField("X", IrType.I32, false)]);
    var held = new IrLocal("p", IrType.Struct(point));
    var own = new IrFunction("O", false, Ints, [held], [], B(new IrFieldStore(new IrLocalRef(held), point.Fields[0], I(1)), new IrReturn(new IrFieldLoad(new IrLocalRef(held), point.Fields[0]))));
    var points = new IrLocal("ps", IrType.Array(IrType.Struct(point)));
    var element = new IrFunction("A", false, Ints, [points], [], B(new IrFieldStore(new IrArrayLoad(new IrLocalRef(points), I(0)), point.Fields[0], I(1)), new IrReturn(I(1))));
    var box = new IrClassType("Box");
    var value = new IrField("V", IrType.I32, false);
    box.Define([value]);
    var reference = new IrLocal("b", IrType.Object(box));
    var shared = new IrFunction("B", false, Ints, [reference], [], B(new IrFieldStore(new IrLocalRef(reference), value, I(1)), new IrReturn(I(1))));
    var isolated = IrAsyncFunctions.Isolated(new IrModule("M", [stepped, copied, faulty, own, element, shared]));
    Assert.That(isolated, Is.EquivalentTo(new[] { "S", "E", "O" }));
  }

  /// <summary>Which functions can fault: a throw, a trapping operation, or a call of one that can.</summary>
  [Test]
  public void FaultFree_OnlyPlainArithmeticAndCalls() {
    var x = new IrLocal("x", IrType.I32);
    var pure = new IrFunction("P", false, Ints, [x], [], B(new IrReturn(new IrBinary(IrBinaryOp.Multiply, new IrLocalRef(x), new IrLocalRef(x)))));
    var divides = new IrFunction("D", false, Ints, [x], [], B(new IrReturn(new IrBinary(IrBinaryOp.Divide, I(1), new IrLocalRef(x)))));
    var checkedSum = new IrFunction("K", false, Ints, [x], [], B(new IrReturn(new IrBinary(IrBinaryOp.AddChecked, I(1), new IrLocalRef(x)))));
    var calls = new IrFunction("C", false, Ints, [x], [], B(new IrReturn(new IrAwait(new IrCall("D", Ints, [new IrLocalRef(x)])))));
    var throws = new IrFunction("T", false, Ints, [], [], B(new IrThrow(new IrNew(IrType.Object(IrExceptionClass.Exception), IrExceptionClass.Exception.Constructors[0], []))));
    var waits = new IrFunction("W", false, Ints, [x], [], B(new IrReturn(new IrAwait(new IrCall("P", Ints, [new IrLocalRef(x)])))));
    var point = new IrStructType("P");
    point.Define([new IrField("X", IrType.I32, false)]);
    var held = new IrLocal("p", IrType.Struct(point));
    var field = new IrFunction("F", false, Ints, [held], [], B(new IrReturn(new IrFieldLoad(new IrLocalRef(held), point.Fields[0]))));
    var stepped = new IrFunction("S", false, Ints, [x], [], B(new IrExprStmt(new IrStep(new IrLocalRef(x), true, false, false)), new IrReturn(new IrLocalRef(x))));
    var negated = new IrFunction("N", false, Ints, [x], [], B(new IrReturn(new IrUnary(IrUnaryOp.NegateChecked, new IrLocalRef(x)))));
    var converted = new IrFunction("V", false, Ints, [], [], B(new IrReturn(new IrConvert(IrType.I32, new IrConst(IrType.I64, 1), IrConversion.Trap))));
    var free = IrAsyncFunctions.FaultFree(new IrModule("M", [pure, divides, checkedSum, calls, throws, waits, field, stepped, negated, converted]));
    Assert.That(free, Is.EquivalentTo(new[] { "P", "W", "F", "S" }));
  }

  [Test]
  public void Format_KeepsEveryAsyncNode() {
    var module = AsyncFixtures.Imported();
    var bytes = IrModuleFormat.Write(module);
    var read = IrModuleFormat.Read(bytes);
    Assert.That(IrModuleFormat.Write(read), Is.EqualTo(bytes));
    Assert.That(read.Functions.Single(function => function.Name == "Valued").ReturnType, Is.EqualTo(IrType.Async(IrType.I32, true)));
    Assert.That(AsyncFixtures.Calls().Select(call => AsyncFixtures.InterpretedOutcome(read, call)), Is.EqualTo(AsyncFixtures.Calls().Select(call => AsyncFixtures.InterpretedOutcome(module, call))));
  }

  /// <summary>Test design 4: a fault before the first await is kept in the async value and thrown where it is awaited, not at the call.</summary>
  [Test]
  public void Interpreter_KeepsAFaultUntilTheAwait() {
    var thrown = new IrThrow(new IrNew(IrType.Object(IrExceptionClass.Exception), IrExceptionClass.Exception.Constructors[1], [new IrStrConst("boom")]));
    var thrower = Async("T", Ints, thrown);
    var caught = new IrLocal("e", IrType.Object(IrExceptionClass.Exception));
    var catcher = new IrFunction("G", true, IrType.Async(IrType.String), [], [caught], B(
      new IrTry(B(new IrExprStmt(new IrAwait(new IrCall("T", Ints, []))), new IrReturn(new IrStrConst("none"))),
        [new IrCatch(IrExceptionClass.Exception, caught, null, B(new IrReturn(new IrStrConst("caught"))))], null)));
    var module = new IrModule("M", [thrower, catcher]);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That(IrInterpreter.Call(module, "G", []), Is.EqualTo("caught"));
    Assert.That(() => IrInterpreter.Call(module, "T", []), Throws.InstanceOf<IrException>().With.Message.EqualTo("boom"));
  }

  [Test]
  public void Interpreter_EndingWithoutAValue_IsAnError() {
    var module = new IrModule("M", [Async("G", Ints)]);
    Assert.That(() => IrInterpreter.Call(module, "G", []), Throws.InvalidOperationException);
    Assert.That(IrInterpreter.Call(new IrModule("M", [Async("G", IrType.Async(IrType.Void))]), "G", []), Is.Null);
  }

  /// <summary>The optimizer keeps every await and inlines no async function into its caller.</summary>
  [Test]
  public void Optimizer_KeepsTheAwaitsAndTheResults() {
    var module = AsyncFixtures.Imported();
    var optimized = Optimizer.Run(module, new OptimizerOptions()).Module;
    Assert.That(IrVerifier.Verify(optimized), Is.Empty);
    Assert.That(optimized.Functions.Select(function => function.Name), Is.SupersetOf(module.Functions.Where(function => function.IsAsync).Select(function => function.Name)));
    Assert.That(AsyncFixtures.Calls().Select(call => AsyncFixtures.InterpretedOutcome(optimized, call)), Is.EqualTo(AsyncFixtures.Calls().Select(AsyncFixtures.DotNetOutcome)));
  }

}
