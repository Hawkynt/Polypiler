using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Exceptions;

/// <summary>
/// Exception handling in the IR on modules built by hand (#1323): the rules of the verifier (test
/// design 11), and what the interpreter does with a finally block on every way out, an exception
/// thrown in a finally block, a rethrow, a filter that throws, and the clauses tested in order (6).
/// </summary>
[TestFixture]
public sealed class IrExceptionTests {

  private static readonly IrType Exception = IrExceptionClass.Reference;

  private static IrConst I(long value) => new(IrType.I32, value);

  private static IrNew New(IrClassType type, string? message = null)
    => new(IrType.Object(type), IrExceptionClass.Constructor(type, message is null ? 0 : 1), message is null ? [] : [new IrStrConst(message)]);

  private static IrFunction F(IrType result, IReadOnlyList<IrLocal> locals, params IrStmt[] statements)
    => new("F", true, result, [], locals, new IrBlock(statements));

  private static IrModule Module(IrFunction function) => new("M", [function], []);

  private static IReadOnlyList<string> Problems(IrFunction function) => [.. IrVerifier.Verify(Module(function)).Select(problem => problem.Message)];

  private static object? Run(IrFunction function) => IrInterpreter.Call(Module(function), "F", []);

  private static IrBlock Block(params IrStmt[] statements) => new(statements);

  [Test]
  public void ThrowAndCatch_OfALibraryClass_AreWellTyped() {
    var caught = new IrLocal("e", IrType.Object(IrExceptionClass.DivideByZeroException));
    var function = F(IrType.I32, [caught],
      new IrTry(Block(new IrThrow(New(IrExceptionClass.DivideByZeroException, "d"))),
        [new IrCatch(IrExceptionClass.DivideByZeroException, caught, new IrIntrinsic(IrIntrinsicOp.StrEquals, IrType.Bool,
          [new IrFieldLoad(new IrLocalRef(caught), IrExceptionClass.Message), new IrStrConst("d")]), Block(new IrReturn(I(1))))], null),
      new IrReturn(I(0)));
    Assert.That(Problems(function), Is.Empty);
    Assert.That(Run(function), Is.EqualTo(1));
  }

  /// <summary>Test design 11: what the verifier refuses of exception handling.</summary>
  [Test]
  public void Verifier_RefusesWhatExceptionHandlingRules() {
    var plain = new IrClassType("Plain");
    plain.Inherit(null, []);
    var number = new IrLocal("n", IrType.I32);
    var caught = new IrLocal("e", Exception);
    var wrong = new IrLocal("w", IrType.Object(IrExceptionClass.OverflowException));
    Assert.That(Problems(F(IrType.Void, [], new IrRethrow())), Has.One.Contains("a rethrow outside a catch clause"));
    Assert.That(Problems(F(IrType.Void, [], new IrThrow(I(1)))), Has.One.Contains("is thrown"));
    Assert.That(Problems(F(IrType.Void, [], new IrTry(Block(), [new IrCatch(plain, null, null, Block())], null))), Has.Some.Contains("no exception class"));
    Assert.That(Problems(F(IrType.Void, [caught], new IrTry(Block(), [new IrCatch(IrExceptionClass.Exception, caught, I(1), Block())], null))),
      Has.One.Contains("the filter of a catch clause"));
    Assert.That(Problems(F(IrType.Void, [wrong], new IrTry(Block(), [new IrCatch(IrExceptionClass.Exception, wrong, null, Block())], null))),
      Has.One.Contains("of a catch clause has the type"));
    Assert.That(Problems(F(IrType.Void, [], new IrTry(Block(), [new IrCatch(IrExceptionClass.Exception, caught, null, Block())], null))),
      Has.One.Contains("is not among the locals"));
    Assert.That(Problems(F(IrType.Void, [], new IrTry(Block(), [], null))), Has.One.Contains("without a catch clause and without a finally block"));
    Assert.That(Problems(F(IrType.I32, [], new IrTry(Block(), [], Block(new IrReturn(I(1)))), new IrReturn(I(0)))), Has.One.Contains("a return inside a finally block"));
    Assert.That(Problems(F(IrType.Void, [], new IrWhile(new IrConst(IrType.Bool, 1), Block(new IrTry(Block(), [], Block(new IrBreak())))))),
      Has.One.Contains("a break outside a loop"));
    Assert.That(Problems(F(IrType.Void, [], new IrWhile(new IrConst(IrType.Bool, 1), Block(new IrTry(Block(), [], Block(new IrContinue())))))),
      Has.One.Contains("a continue outside a loop"));
    Assert.That(Problems(F(IrType.Void, [], new IrWhile(new IrConst(IrType.Bool, 0), Block(new IrTry(Block(), [], Block(new IrWhile(new IrConst(IrType.Bool, 0), Block(new IrBreak())))))))),
      Is.Empty, "a loop inside the finally block may be left");
    Assert.That(Problems(F(IrType.I32, [], new IrReturn(new IrThrowExpression(IrType.I32, I(2))))), Has.One.Contains("is thrown"));
    Assert.That(Problems(F(IrType.Void, [], new IrExprStmt(new IrThrowExpression(IrType.Void, New(IrExceptionClass.Exception))))), Has.One.Contains("a throw stands for a value of the type"));
    Assert.That(Problems(F(IrType.Void, [], new IrExprStmt(new IrNew(Exception, IrExceptionClass.Constructor(IrExceptionClass.Exception, 1), [I(1)])))),
      Has.One.Contains("do not match its parameters"));
    Assert.That(Problems(F(IrType.Void, [], new IrExprStmt(new IrNew(Exception, IrExceptionClass.Constructor(IrExceptionClass.ArithmeticException, 0), [])))),
      Has.One.Contains("a constructor that is not one of Exception"));
    Assert.That(Problems(F(IrType.Void, [number], new IrDeclare(number, I(0)), new IrExprStmt(new IrCall(IrExceptionClass.Constructor(IrExceptionClass.Exception, 0).Function, IrType.Void, [])))),
      Has.One.Contains("is called other than first by a constructor"));
    Assert.That(Problems(F(IrType.Void, [caught], new IrTry(Block(), [new IrCatch(IrExceptionClass.Exception, caught, null, Block(new IrDeclare(caught, null)))], null))),
      Has.One.Contains("declared twice"));
  }

  /// <summary>Test design 6: a finally block runs once on every way out, the value of a return computed before it.</summary>
  [Test]
  public void Finally_RunsOnEveryWayOut() {
    var count = new IrLocal("c", IrType.I32);
    var index = new IrLocal("i", IrType.I32);
    IrStmt Count() => new IrAssign(count, new IrBinary(IrBinaryOp.Add, new IrLocalRef(count), I(1)));
    IrExpr Counted() => new IrLocalRef(count);
    // On a return: the value is the count before the finally block adds one.
    Assert.That(Run(F(IrType.I32, [count], new IrDeclare(count, I(5)), new IrTry(Block(new IrReturn(Counted())), [], Block(Count())))), Is.EqualTo(5));
    // On normal exit, then on a break out of a loop: once each.
    Assert.That(Run(F(IrType.I32, [count, index], new IrDeclare(count, I(0)),
      new IrTry(Block(), [], Block(Count())),
      new IrWhile(new IrConst(IrType.Bool, 1), Block(new IrTry(Block(new IrBreak()), [], Block(Count())))),
      new IrReturn(Counted()))), Is.EqualTo(2));
    // On an exception caught outside: once, and the handler sees it.
    Assert.That(Run(F(IrType.I32, [count], new IrDeclare(count, I(0)),
      new IrTry(Block(new IrTry(Block(new IrThrow(New(IrExceptionClass.Exception))), [], Block(Count()))), [new IrCatch(IrExceptionClass.Exception, null, null, Block(Count()))], null),
      new IrReturn(Counted()))), Is.EqualTo(2));
  }

  /// <summary>Test design 6: an exception thrown in a finally block replaces the one in flight.</summary>
  [Test]
  public void ExceptionInFinally_ReplacesTheOneInFlight() {
    var caught = new IrLocal("e", Exception);
    var function = F(IrType.String, [caught],
      new IrTry(Block(new IrTry(Block(new IrThrow(New(IrExceptionClass.Exception, "first"))), [], Block(new IrThrow(New(IrExceptionClass.Exception, "second"))))),
        [new IrCatch(IrExceptionClass.Exception, caught, null, Block(new IrReturn(new IrFieldLoad(new IrLocalRef(caught), IrExceptionClass.Message))))], null),
      new IrReturn(new IrStrConst("none")));
    Assert.That(Run(function), Is.EqualTo("second"));
  }

  /// <summary>A rethrow throws the very object caught; an exception nothing catches leaves the call as an <see cref="IrException"/>.</summary>
  [Test]
  public void Rethrow_ThrowsTheObjectCaught() {
    var function = F(IrType.Void, [], new IrTry(Block(new IrThrow(New(IrExceptionClass.InvalidOperationException, "kept"))),
      [new IrCatch(IrExceptionClass.Exception, null, null, Block(new IrRethrow()))], null));
    var thrown = Assert.Throws<IrException>(() => Run(function))!;
    Assert.That(thrown.Message, Is.EqualTo("kept"));
    Assert.That(thrown.Class, Is.SameAs(IrExceptionClass.InvalidOperationException));
    Assert.That(thrown, Is.Not.InstanceOf<IrTrapException>());
  }

  /// <summary>The clauses are tested in order; the first that selects the exception handles it, an unrelated one lets it pass; a filter that throws is false.</summary>
  [Test]
  public void Clauses_AreTestedInOrder() {
    IrFunction Classify(IrClassType thrown, IrExpr? filter) => F(IrType.I32, [],
      new IrTry(Block(new IrThrow(New(thrown))), [
        new IrCatch(IrExceptionClass.IndexOutOfRangeException, null, null, Block(new IrReturn(I(1)))),
        new IrCatch(IrExceptionClass.ArithmeticException, null, filter, Block(new IrReturn(I(2)))),
        new IrCatch(IrExceptionClass.Exception, null, null, Block(new IrReturn(I(3))))
      ], null));
    Assert.That(Run(Classify(IrExceptionClass.IndexOutOfRangeException, null)), Is.EqualTo(1));
    Assert.That(Run(Classify(IrExceptionClass.OverflowException, null)), Is.EqualTo(2));
    Assert.That(Run(Classify(IrExceptionClass.NullReferenceException, null)), Is.EqualTo(3));
    Assert.That(Run(Classify(IrExceptionClass.OverflowException, new IrConst(IrType.Bool, 0))), Is.EqualTo(3));
    var throwing = new IrBinary(IrBinaryOp.Equal, new IrBinary(IrBinaryOp.Divide, I(1), new IrBinary(IrBinaryOp.Subtract, I(1), I(1))), I(0));
    Assert.That(Run(Classify(IrExceptionClass.OverflowException, throwing)), Is.EqualTo(3), "a filter that traps is false");
  }

  /// <summary>A trap is caught as an object of its class with the trap's message; a throw of null traps.</summary>
  [Test]
  public void Trap_IsCaughtAsAnObjectOfItsClass() {
    var caught = new IrLocal("e", IrType.Object(IrExceptionClass.DivideByZeroException));
    var division = F(IrType.String, [caught],
      new IrTry(Block(new IrExprStmt(new IrBinary(IrBinaryOp.Divide, I(1), new IrBinary(IrBinaryOp.Subtract, I(1), I(1)))), new IrReturn(new IrStrConst("none"))),
        [new IrCatch(IrExceptionClass.DivideByZeroException, caught, null, Block(new IrReturn(new IrFieldLoad(new IrLocalRef(caught), IrExceptionClass.Message))))], null));
    Assert.That(Run(division), Is.EqualTo("divide by zero"));
    var nothing = F(IrType.Void, [], new IrThrow(new IrNull(Exception)));
    Assert.That(Assert.Throws<IrTrapException>(() => Run(nothing))!.Trap, Is.EqualTo(IrTrap.NullReference));
  }

  /// <summary>A throw expression throws where a value stands.</summary>
  [Test]
  public void ThrowExpression_Throws() {
    var function = F(IrType.I32, [], new IrReturn(new IrConditional(new IrConst(IrType.Bool, 0), I(1), new IrThrowExpression(IrType.I32, New(IrExceptionClass.ArgumentException, "a")))));
    Assert.That(Problems(function), Is.Empty);
    Assert.That(Assert.Throws<IrException>(() => Run(function))!.Class, Is.SameAs(IrExceptionClass.ArgumentException));
  }

  /// <summary>A library exception made without a message has .NET's own.</summary>
  [Test]
  public void LibraryException_WithoutAMessage_HasItsOwn() {
    var caught = new IrLocal("e", Exception);
    var function = F(IrType.String, [caught], new IrTry(Block(new IrThrow(New(IrExceptionClass.OverflowException))),
      [new IrCatch(IrExceptionClass.Exception, caught, null, Block(new IrReturn(new IrFieldLoad(new IrLocalRef(caught), IrExceptionClass.Message))))], null),
      new IrReturn(new IrStrConst("none")));
    Assert.That(Run(function), Is.EqualTo(new System.OverflowException().Message));
  }

}
