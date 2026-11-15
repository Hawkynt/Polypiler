using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Sequences;

/// <summary>
/// Lazy sequences in the IR (#1327) on modules built by hand: the type, the rules of the verifier, the
/// byte format and what the interpreter does - laziness, restarting, closing on every exit, nested
/// closing and the element types.
/// </summary>
[TestFixture]
public sealed class IrSequencesTests {

  private static readonly IrType Ints = IrType.Sequence(IrType.I32, true);

  private static IrConst I(long value) => new(IrType.I32, value);

  private static IrLocalRef R(IrLocal local) => new(local);

  private static IrBlock B(params IrStmt[] statements) => new(statements);

  private static IReadOnlyList<string> Problems(params IrFunction[] functions) => [.. IrVerifier.Verify(new IrModule("M", functions)).Select(diagnostic => diagnostic.Message)];

  private static IrFunction Iterator(IrType type, IReadOnlyList<IrLocal> locals, params IrStmt[] statements) => new("G", false, type, [], [.. locals], B(statements));

  [Test]
  public void Type_NamesItsElementAndWhetherItRestarts() {
    Assert.That(Ints.ToString(), Is.EqualTo("seq<i32>"));
    Assert.That(IrType.Sequence(IrType.F64, false).ToString(), Is.EqualTo("seq<f64, single-pass>"));
    Assert.That(Ints.IsLazySequence && Ints.Restartable && !IrType.Sequence(IrType.I32, false).Restartable, Is.True);
    Assert.That(Ints, Is.Not.EqualTo(IrType.Sequence(IrType.I32, false)));
    Assert.That(Ints, Is.EqualTo(IrType.Sequence(IrType.I32, true)));
    Assert.That(IrType.Array(IrType.I32).IsLazySequence || IrType.Array(IrType.I32).Restartable, Is.False);
    Assert.That(Ints.IsNullable, Is.False);
  }

  [Test]
  public void Type_HoldsValuesButNoViewsOptionalsOrSequences() {
    var box = new IrClassType("Box");
    box.Define([]);
    Assert.That(Ints.IsStorable && IrType.Sequence(IrType.String, true).IsStorable && IrType.Sequence(IrType.Object(box), false).IsStorable, Is.True);
    Assert.That(IrType.Sequence(IrType.List(IrType.I32), true).IsStorable && IrType.Sequence(IrType.Array(IrType.U8), true).IsStorable, Is.True);
    Assert.That(IrType.Sequence(IrType.Span(IrType.U8), true).IsStorable, Is.False);
    Assert.That(IrType.Sequence(IrType.Optional(IrType.I32), true).IsStorable, Is.False);
    Assert.That(IrType.Sequence(Ints, true).IsStorable, Is.False);
    Assert.That(IrType.Sequence(IrType.Void, true).IsStorable, Is.False);
  }

  [Test]
  public void Module_IsWellTyped() => Assert.That(IrVerifier.Verify(SequenceModules.Module()).Select(diagnostic => diagnostic.Message), Is.Empty);

  [Test]
  public void Format_KeepsEverySequenceNode() {
    var module = SequenceModules.Module();
    var bytes = IrModuleFormat.Write(module);
    var read = IrModuleFormat.Read(bytes);
    Assert.That(IrModuleFormat.Write(read), Is.EqualTo(bytes));
    Assert.That(SequenceModules.Calls().Select(call => PrototypeFixture.InterpretedOutcome(read, call)), Is.EqualTo(SequenceModules.Calls().Select(call => PrototypeFixture.InterpretedOutcome(module, call))));
  }

  /// <summary>Test design 2, 3, 4, 5, 6 and 7: the values and the event traces the interpreter computes.</summary>
  [TestCase("Lazy", 57189)]
  [TestCase("EarlyBreak", 57189)]
  [TestCase("Full", 718283896)]
  [TestCase("ReturnInside", 712924)]
  [TestCase("ThrowInside", 7194)]
  [TestCase("ThrowInBody", 1894)]
  [TestCase("Twice", 6)]
  [TestCase("TwiceOnce", 3)]
  [TestCase("ResumeOnce", 7119)]
  [TestCase("NestedClose", 189)]
  [TestCase("NestedFull", 128339)]
  [TestCase("Loops", 2463219)]
  [TestCase("Switched", 52030)]
  [TestCase("Empty", 5)]
  [TestCase("Delegated", 124567)]
  [TestCase("FromArray", 459)]
  [TestCase("ArrayLoop", 33)]
  [TestCase("Floats", 112)]
  [TestCase("Truths", 121)]
  [TestCase("Objects", 34)]
  [TestCase("Doubled", 2485)]
  [TestCase("DoubledClose", 285)]
  [TestCase("Counted", 2121)]
  [TestCase("FibSum", 512559679)]
  [TestCase("Closure", 30)]
  [TestCase("Repeated", 18289)]
  [TestCase("Picked", 70800)]
  [TestCase("Listed", 2461312)]
  [TestCase("ListedBreak", 2)]
  [TestCase("Modified", 102)]
  [TestCase("Mapped", 1256)]
  [TestCase("PassWhile", 30)]
  [TestCase("PassSequence", 60)]
  [TestCase("PassFor", 30)]
  [TestCase("PassList", 150)]
  public void Interpreter_RunsTheModule(string function, int expected)
    => Assert.That(IrInterpreter.Call(SequenceModules.Module(), function, []), Is.EqualTo(expected));

  [Test]
  public void Interpreter_AddsLongs() => Assert.That(IrInterpreter.Call(SequenceModules.Module(), "Longs", []), Is.EqualTo((1L << 40) - 3));

  [Test]
  public void Interpreter_HandsTheSequenceOutUnrun() {
    var t = new IrLocal("t", IrType.Array(IrType.I32));
    var module = new IrModule("M", [new IrFunction("G", true, Ints, [t], [], B(new IrStore(R(t), I(0), I(1)), new IrYield(I(1))))]);
    var trace = new int[1];
    Assert.That(IrInterpreter.Call(module, "G", [trace]), Is.Not.Null);
    Assert.That(trace[0], Is.Zero);
  }

  [Test]
  public void Verifier_RefusesAYieldOutsideAnIterator()
    => Assert.That(Problems(new IrFunction("F", true, IrType.Void, [], [], B(new IrYield(I(1)), new IrYieldBreak()))), Has.Some.Contains("a yield outside an iterator function"));

  [Test]
  public void Verifier_RefusesAYieldOfAnotherType()
    => Assert.That(Problems(Iterator(Ints, [], new IrYield(new IrConst(IrType.I64, 1)))), Has.Some.Contains("a yield of the type i64 in an iterator of seq<i32>"));

  [Test]
  public void Verifier_RefusesADelegationToAnotherType() {
    var doubles = IrType.Sequence(IrType.F64, true);
    Assert.That(Problems(Iterator(Ints, [], new IrYieldFrom(new IrArraySequence(new IrNewArray(IrType.Array(IrType.F64), I(1)))))),
      Has.Some.Contains($"a yield of the values of the type {doubles} in an iterator of seq<i32>"));
    Assert.That(Problems(Iterator(Ints, [], new IrYieldFrom(I(1)))), Has.Some.Contains("a yield of the values of the type i32"));
  }

  [TestCase("catch", "a yield inside a catch clause")]
  [TestCase("finally", "a yield inside a finally block")]
  [TestCase("guarded", "a yield inside a try that has a catch clause")]
  public void Verifier_RefusesAYieldWhereCSharpHasNoForm(string place, string message) {
    var yielded = B(new IrYield(I(1)));
    var clause = new IrCatch(IrExceptionClass.Exception, null, null, place == "catch" ? yielded : B());
    var attempt = place switch {
      "catch" => new IrTry(B(), [clause], null),
      "finally" => new IrTry(B(), [], yielded),
      _ => new IrTry(yielded, [clause], null)
    };
    Assert.That(Problems(Iterator(Ints, [], attempt)), Has.Some.Contains(message));
  }

  [Test]
  public void Verifier_AcceptsAYieldInATryWithAFinallyBlockOnly()
    => Assert.That(Problems(Iterator(Ints, [], new IrTry(B(new IrYield(I(1))), [], B()))), Is.Empty);

  [Test]
  public void Verifier_RefusesAReturnWithAValueInAnIterator() {
    var other = new IrFunction("H", false, Ints, [], [], B(new IrYieldBreak()));
    Assert.That(Problems(Iterator(Ints, [], new IrReturn(new IrCall("H", Ints, []))), other), Has.Some.Contains("a return with a value in an iterator function"));
    Assert.That(Problems(Iterator(Ints, [], new IrReturn(null))), Is.Empty);
  }

  [Test]
  public void Verifier_RefusesAYieldInsideALambda() {
    var lambda = new IrLambda(IrType.Function(Ints), [], [], [], B(new IrYield(I(1))));
    Assert.That(Problems(Iterator(Ints, [], new IrExprStmt(new IrInvoke(Ints, lambda, [])), new IrYieldBreak())), Has.Some.Contains("a yield inside a lambda"));
  }

  [Test]
  public void Verifier_RefusesAnIteratorOfNoElementType()
    => Assert.That(Problems(Iterator(IrType.Sequence(IrType.Span(IrType.U8), true), [])), Has.Some.Contains("the iterator returns the type seq<span<u8>>"));

  [Test]
  public void Verifier_ChecksTheLoopOverASequence() {
    var v = new IrLocal("v", IrType.I32);
    var d = new IrLocal("d", IrType.F64);
    var g = new IrFunction("H", false, Ints, [], [], B(new IrYieldBreak()));
    IrFunction Loop(IrLocal element, IrExpr sequence, params IrLocal[] locals) => new("F", true, IrType.Void, [], locals, B(new IrForEachSequence(element, sequence, B(new IrBreak(), new IrContinue()))));
    Assert.That(Problems(Loop(v, new IrCall("H", Ints, []), v), g), Is.Empty);
    Assert.That(Problems(Loop(d, new IrCall("H", Ints, []), d), g), Has.Some.Contains("the element 'd' of a loop over a seq<i32> has the type f64"));
    Assert.That(Problems(Loop(v, new IrCall("H", Ints, [])), g), Has.Some.Contains("local 'v' is not among the locals of the function"));
    Assert.That(Problems(Loop(v, I(3), v)), Has.Some.Contains("a loop over the sequence of a value of the type i32, which is no sequence"));
    var twice = new IrFunction("F", true, IrType.Void, [], [v], B(new IrDeclare(v, I(1)), new IrForEachSequence(v, new IrCall("H", Ints, []), B())));
    Assert.That(Problems(twice, g), Has.Some.Contains("local 'v' is declared twice"));
  }

  [Test]
  public void Verifier_ChecksTheArrayOfASequence() {
    Assert.That(Problems(new IrFunction("F", true, Ints, [], [], B(new IrYieldFrom(new IrArraySequence(I(1)))))), Has.Some.Contains("a sequence of the elements of a value of the type i32, which is no array"));
    var forged = new IrArraySequence(new IrNewArray(IrType.Array(IrType.I32), I(1))) with { Type = IrType.Sequence(IrType.I32, false) };
    Assert.That(Problems(new IrFunction("F", true, IrType.Sequence(IrType.I32, false), [], [], B(new IrYieldFrom(forged)))), Has.Some.Contains("has the type seq<i32, single-pass>"));
  }

  [Test]
  public void Suspends_FindsYieldsInEveryBlockButNotInLambdas() {
    Assert.That(IrSequences.Suspends(B(new IrIf(new IrConst(IrType.Bool, 1), B(new IrYield(I(1))), null))), Is.True);
    Assert.That(IrSequences.Suspends(new IrTry(B(), [], B(new IrYieldFrom(new IrArraySequence(new IrNewArray(IrType.Array(IrType.I32), I(0))))))), Is.True);
    Assert.That(IrSequences.Suspends(B(new IrYieldBreak(), new IrReturn(null))), Is.False);
    Assert.That(IrSequences.Suspends(new IrExprStmt(new IrLambda(IrType.Function(IrType.Void), [], [], [], B(new IrYield(I(1)))))), Is.False);
    Assert.That(IrSequences.Statements(B(new IrWhile(new IrConst(IrType.Bool, 1), B(new IrYieldBreak())))).Count(), Is.EqualTo(3));
  }

  [Test]
  public void Tree_ListsTheParts() {
    var v = new IrLocal("v", IrType.I32);
    var array = new IrNewArray(IrType.Array(IrType.I32), I(1));
    var sequence = new IrArraySequence(array);
    var loop = new IrForEachSequence(v, sequence, B());
    Assert.That(IrTree.Operands(sequence), Is.EqualTo(new[] { array }));
    Assert.That(IrTree.Expressions(loop), Is.EqualTo(new[] { sequence }));
    Assert.That(IrTree.Blocks(loop), Is.EqualTo(new[] { loop.Body }));
    Assert.That(IrTree.Expressions(new IrYield(I(2))), Is.EqualTo(new[] { I(2) }));
    Assert.That(IrTree.Expressions(new IrYieldFrom(sequence)), Is.EqualTo(new[] { sequence }));
    Assert.That(IrTree.Expressions(new IrYieldBreak()), Is.Empty);
  }

}
