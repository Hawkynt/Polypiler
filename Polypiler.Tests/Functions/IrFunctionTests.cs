using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Functions;

/// <summary>
/// Function values in the IR on modules built by hand: closures over shared variables, per-iteration
/// loop variables, functions and methods as values, calls of function values and what the verifier
/// refuses of them (#1326).
/// </summary>
[TestFixture]
public sealed class IrFunctionTests {

  private static readonly IrType _ints = IrType.Function(IrType.I32);
  private static readonly IrType _unary = IrType.Function(IrType.I32, IrType.I32);

  private static IrConst I(long value) => new(IrType.I32, value);

  private static IrLocalRef R(IrLocal local) => new(local);

  private static IrBinary Add(IrExpr left, IrExpr right) => new(IrBinaryOp.Add, left, right);

  private static IrFunction F(string name, IrType result, IReadOnlyList<IrLocal> parameters, IReadOnlyList<IrLocal> locals, params IrStmt[] statements)
    => new(name, true, result, parameters, locals, new IrBlock(statements));

  private static IReadOnlyList<string> Problems(IrModule module) => [.. IrVerifier.Verify(module).Select(diagnostic => diagnostic.Message)];

  private static object? Run(IrModule module, string name = "F", params object[] arguments) {
    Assert.That(Problems(module), Is.Empty);
    return IrInterpreter.Call(module, name, arguments);
  }

  /// <summary>() => ++n over a local n, as MakeCounter returns it.</summary>
  private static (IrFunction Function, IrLocal Counter) MakeCounter() {
    var n = new IrLocal("n", IrType.I32);
    var counter = IrLambda.Of([], IrType.I32, [], new IrBlock([new IrReturn(new IrStep(R(n), true, false, false))]));
    return (F("MakeCounter", _ints, [], [n], new IrDeclare(n, I(0)), new IrReturn(counter)), n);
  }

  [Test]
  public void FunctionType_IsStructuralAndNamedBySignature() {
    Assert.That(IrType.Function(IrType.I32, IrType.I32), Is.EqualTo(_unary));
    Assert.That(IrType.Function(IrType.I32, IrType.I32).GetHashCode(), Is.EqualTo(_unary.GetHashCode()));
    Assert.That(IrType.Function(IrType.I64, IrType.I32), Is.Not.EqualTo(_unary));
    Assert.That(IrType.Function(IrType.I32, IrType.I32, IrType.I32), Is.Not.EqualTo(_unary));
    Assert.That(_unary.ToString(), Is.EqualTo("fn(i32) -> i32"));
    Assert.That(IrType.Function(IrType.Void).ToString(), Is.EqualTo("fn() -> void"));
    Assert.That(_unary.IsFunction && _unary.IsNullable && _unary.IsStorable && IrType.Array(_unary).IsStorable, Is.True);
    Assert.That(new IrFunctionType([IrType.I32], IrType.I32).Equals((object?)null), Is.False);
  }

  /// <summary>Counter() = 123 and Interleaved() = 21: each closure has a variable of its own, shared by its calls.</summary>
  [Test]
  public void Closure_SharesTheVariableItCaptures() {
    var (make, _) = MakeCounter();
    var a = new IrLocal("a", _ints);
    var b = new IrLocal("b", _ints);
    IrExpr Call(IrLocal f) => new IrInvoke(IrType.I32, R(f), []);
    var counter = F("Counter", IrType.I32, [], [a], new IrDeclare(a, new IrCall("MakeCounter", _ints, [])),
      new IrReturn(Add(Add(new IrBinary(IrBinaryOp.Multiply, Call(a), I(100)), new IrBinary(IrBinaryOp.Multiply, Call(a), I(10))), Call(a))));
    var interleaved = F("Interleaved", IrType.I32, [], [a, b], new IrDeclare(a, new IrCall("MakeCounter", _ints, [])), new IrExprStmt(Call(a)),
      new IrDeclare(b, new IrCall("MakeCounter", _ints, [])), new IrReturn(Add(new IrBinary(IrBinaryOp.Multiply, Call(a), I(10)), Call(b))));
    var module = new IrModule("M", [make, counter, interleaved]);
    Assert.That(Run(module, "Counter"), Is.EqualTo(123));
    Assert.That(Run(module, "Interleaved"), Is.EqualTo(21));
  }

  /// <summary>Late(2) = 10: a write after the closure is made is seen by it.</summary>
  [Test]
  public void Closure_SeesALaterWrite() {
    var x = new IrLocal("x", IrType.I32);
    var g = new IrLocal("g", _ints);
    var module = new IrModule("M", [F("F", IrType.I32, [x], [g],
      new IrDeclare(g, IrLambda.Of([], IrType.I32, [], new IrBlock([new IrReturn(R(x))]))),
      new IrAssign(x, new IrBinary(IrBinaryOp.Multiply, R(x), I(5))),
      new IrReturn(new IrInvoke(IrType.I32, R(g), [])))]);
    Assert.That(Run(module, "F", 2), Is.EqualTo(10));
  }

  /// <summary>F(4) = 13: a closure that assigns a variable of its function by a binding expression captures it, and the function sees the write.</summary>
  [Test]
  public void Closure_CapturesTheVariableItBinds() {
    var x = new IrLocal("x", IrType.I32);
    var y = new IrLocal("y", IrType.I32);
    var g = new IrLocal("g", _ints);
    var bind = IrLambda.Of([], IrType.I32, [], new IrBlock([new IrReturn(new IrBind(y, Add(R(x), I(1))))]));
    Assert.That(bind.Captures, Is.EqualTo(new[] { y, x }));
    var module = new IrModule("M", [F("F", IrType.I32, [x], [y, g],
      new IrDeclare(y, I(0)),
      new IrDeclare(g, bind),
      new IrExprStmt(new IrInvoke(IrType.I32, R(g), [])),
      new IrReturn(Add(R(y), new IrBinary(IrBinaryOp.Multiply, R(x), I(2)))))]);
    Assert.That(Run(module, "F", 4), Is.EqualTo(13));
  }

  /// <summary>
  /// Three closures made in three passes of a loop over i, each returning i: 9 when the loop shares
  /// its variable (a C# for), 3 when every pass has its own (a JavaScript for-let) - also when the body
  /// assigns it, which the next pass starts from.
  /// </summary>
  [TestCase(false, false, 9)]
  [TestCase(true, false, 3)]
  [TestCase(true, true, 9)]
  public void For_SharesOrRenewsItsVariable(bool perIteration, bool bodyAssigns, int expected) {
    var i = new IrLocal("i", IrType.I32);
    var fs = new IrLocal("fs", IrType.Array(_ints));
    var k = new IrLocal("k", IrType.I32);
    var closure = IrLambda.Of([], IrType.I32, [], new IrBlock([new IrReturn(R(i))]));
    List<IrStmt> body = [new IrStore(R(fs), new IrStep(R(k), true, true, false), closure)];
    if (bodyAssigns)
      body.Add(new IrExprStmt(new IrStep(R(i), true, false, false)));

    IrExpr At(int index) => new IrInvoke(IrType.I32, new IrArrayLoad(R(fs), I(index)), []);
    var loop = new IrFor(new IrBinary(IrBinaryOp.Less, R(i), I(bodyAssigns ? 6 : 3)), new IrBlock(body), new IrBlock([new IrExprStmt(new IrStep(R(i), true, false, false))])) {
      PerIteration = perIteration ? [i] : []
    };
    var module = new IrModule("M", [F("F", IrType.I32, [], [fs, k, i],
      new IrDeclare(fs, new IrNewArray(IrType.Array(_ints), I(3))), new IrDeclare(k, I(0)), new IrDeclare(i, I(0)), loop,
      new IrReturn(Add(Add(At(0), At(1)), At(2))))]);
    Assert.That(Run(module), Is.EqualTo(expected));
  }

  /// <summary>A local declared in a loop body is a new variable every pass, as a C# foreach variable is.</summary>
  [Test]
  public void BodyDeclaredLocal_IsNewEveryPass() {
    var t = new IrLocal("t", IrType.I32);
    var v = new IrLocal("v", IrType.I32);
    var fs = new IrLocal("fs", IrType.Array(_ints));
    IrExpr At(int index) => new IrInvoke(IrType.I32, new IrArrayLoad(R(fs), I(index)), []);
    var module = new IrModule("M", [F("F", IrType.I32, [], [fs, t, v],
      new IrDeclare(fs, new IrNewArray(IrType.Array(_ints), I(3))), new IrDeclare(t, I(0)),
      new IrWhile(new IrBinary(IrBinaryOp.Less, R(t), I(3)), new IrBlock([
        new IrDeclare(v, R(t)),
        new IrStore(R(fs), R(t), IrLambda.Of([], IrType.I32, [], new IrBlock([new IrReturn(R(v))]))),
        new IrAssign(t, Add(R(t), I(1)))
      ])),
      new IrReturn(Add(Add(At(0), At(1)), At(2))))]);
    Assert.That(Run(module), Is.EqualTo(3));
  }

  /// <summary>Fact through a recursive local function, which captures its own variable; Fact(13) wraps as an int.</summary>
  [TestCase(0, 1)]
  [TestCase(1, 1)]
  [TestCase(5, 120)]
  [TestCase(12, 479001600)]
  [TestCase(13, 1932053504)]
  public void LocalFunction_RecursesThroughItsOwnCapture(int n, int expected) {
    var parameter = new IrLocal("n", IrType.I32);
    var f = new IrLocal("f", _unary);
    var m = new IrLocal("m", IrType.I32);
    var body = new IrBlock([new IrReturn(new IrConditional(new IrBinary(IrBinaryOp.LessOrEqual, R(m), I(1)), I(1),
      new IrBinary(IrBinaryOp.Multiply, R(m), new IrInvoke(IrType.I32, R(f), [new IrBinary(IrBinaryOp.Subtract, R(m), I(1))]))))]);
    var lambda = IrLambda.Of([m], IrType.I32, [], body);
    Assert.That(lambda.Captures, Is.EqualTo(new[] { f }));
    var module = new IrModule("M", [F("F", IrType.I32, [parameter], [f], new IrDeclare(f, null), new IrAssign(f, lambda), new IrReturn(new IrInvoke(IrType.I32, R(f), [R(parameter)])))]);
    Assert.That(Run(module, "F", n), Is.EqualTo(expected));
  }

  /// <summary>Composed(4) = 15: closures returned from a function, capturing its parameters, two levels deep.</summary>
  [Test]
  public void NestedClosures_CaptureThroughEachLevel() {
    var f = new IrLocal("f", _unary);
    var g = new IrLocal("g", _unary);
    var v = new IrLocal("v", IrType.I32);
    var compose = F("Compose", _unary, [f, g], [], new IrReturn(IrLambda.Of([v], IrType.I32, [], new IrBlock([
      new IrReturn(new IrInvoke(IrType.I32, R(g), [new IrInvoke(IrType.I32, R(f), [R(v)])]))]))));
    var x = new IrLocal("x", IrType.I32);
    var a = new IrLocal("v", IrType.I32);
    var b = new IrLocal("v", IrType.I32);
    var composed = F("Composed", IrType.I32, [x], [], new IrReturn(new IrInvoke(IrType.I32, new IrCall("Compose", _unary, [
      IrLambda.Of([a], IrType.I32, [], new IrBlock([new IrReturn(Add(R(a), I(1)))])),
      IrLambda.Of([b], IrType.I32, [], new IrBlock([new IrReturn(new IrBinary(IrBinaryOp.Multiply, R(b), I(3)))]))]), [R(x)])));
    Assert.That(((IrLambda)((IrReturn)compose.Body.Statements[0]).Value!).Captures, Is.EqualTo(new[] { g, f }));
    Assert.That(Run(new IrModule("M", [compose, composed]), "Composed", 4), Is.EqualTo(15));

    var outer = new IrLocal("o", IrType.I32);
    var inner = IrLambda.Of([], IrType.I32, [], new IrBlock([new IrReturn(R(outer))]));
    var middle = IrLambda.Of([], _ints, [], new IrBlock([new IrReturn(inner)]));
    Assert.That(middle.Captures, Is.EqualTo(new[] { outer }));
    var deep = F("F", IrType.I32, [], [outer], new IrDeclare(outer, I(41)), new IrReturn(Add(new IrInvoke(IrType.I32, new IrInvoke(_ints, middle, []), []), I(1))));
    Assert.That(Run(new IrModule("M", [deep])), Is.EqualTo(42));
  }

  /// <summary>MethodGroup(-7) = 49: a function of the module as a value.</summary>
  [Test]
  public void FunctionRef_CallsTheFunction() {
    var v = new IrLocal("v", IrType.I32);
    var square = new IrFunction("Square", false, IrType.I32, [v], [], new IrBlock([new IrReturn(new IrBinary(IrBinaryOp.Multiply, R(v), R(v)))]));
    var x = new IrLocal("x", IrType.I32);
    var f = new IrLocal("f", _unary);
    var module = new IrModule("M", [square, F("F", IrType.I32, [x], [f], new IrDeclare(f, new IrFunctionRef(_unary, "Square")), new IrReturn(new IrInvoke(IrType.I32, R(f), [R(x)])))]);
    Assert.That(Run(module, "F", -7), Is.EqualTo(49));
  }

  /// <summary>A null callee traps after its arguments are evaluated; a function value compares with null by reference.</summary>
  [Test]
  public void NullCallee_TrapsAfterTheArguments() {
    var f = new IrLocal("f", _unary);
    var seen = new IrLocal("seen", IrType.I32);
    var probe = IrLambda.Of([], IrType.I32, [], new IrBlock([new IrAssign(seen, I(1)), new IrReturn(I(0))]));
    var module = new IrModule("M", [
      F("F", IrType.I32, [], [f, seen], new IrDeclare(seen, I(0)), new IrDeclare(f, new IrNull(_unary)),
        new IrReturn(new IrInvoke(IrType.I32, R(f), [new IrInvoke(IrType.I32, probe, [])]))),
      F("G", IrType.Bool, [], [f], new IrDeclare(f, new IrNull(_unary)), new IrReturn(new IrReferenceEquals(R(f), new IrNull(_unary))))
    ]);
    Assert.That(() => Run(module), Throws.TypeOf<IrTrapException>().With.Property(nameof(IrTrapException.Trap)).EqualTo(IrTrap.NullReference));
    Assert.That(Run(module, "G"), Is.EqualTo(true));
  }

  /// <summary>A method bound to its receiver: evaluated once, dispatched as the call kind says; a null receiver traps when the value is made.</summary>
  [Test]
  public void BoundMethod_KeepsItsReceiver() {
    var type = new IrClassType("K");
    var field = new IrField("v", IrType.I32, false);
    var constructor = new IrConstructor("K.ctor");
    var get = new IrMethod("Get", "K.Get", false);
    var make = new IrMethod("Make", "K.Make", true);
    type.Define([field]);
    type.DefineMembers([constructor], [get, make], [], []);
    var self = new IrLocal("this", IrType.Object(type));
    var start = new IrLocal("start", IrType.I32);
    var reader = new IrLocal("this", IrType.Object(type));
    var functions = new List<IrFunction> {
      new("K.ctor", false, IrType.Void, [self, start], [], new IrBlock([new IrFieldStore(R(self), field, R(start))])),
      new("K.Get", false, IrType.I32, [reader], [], new IrBlock([new IrReturn(new IrFieldLoad(R(reader), field))])),
      new("K.Make", false, IrType.I32, [], [], new IrBlock([new IrReturn(I(5))]))
    };
    var k = new IrLocal("k", IrType.Object(type));
    var f = new IrLocal("f", _ints);
    functions.Add(F("F", IrType.I32, [], [k, f], new IrDeclare(k, new IrNew(IrType.Object(type), constructor, [I(7)])),
      new IrDeclare(f, new IrBoundMethod(_ints, R(k), get)), new IrAssign(k, new IrNull(IrType.Object(type))),
      new IrReturn(Add(new IrInvoke(IrType.I32, R(f), []), new IrInvoke(IrType.I32, new IrBoundMethod(_ints, null, make), [])))));
    functions.Add(F("G", IrType.Void, [], [k, f], new IrDeclare(k, new IrNull(IrType.Object(type))), new IrDeclare(f, new IrBoundMethod(_ints, R(k), get))));
    var module = new IrModule("M", functions) { Types = [type] };
    Assert.That(Run(module), Is.EqualTo(12));
    Assert.That(() => IrInterpreter.Call(module, "G", []), Throws.TypeOf<IrTrapException>());
  }

  /// <summary>Test design 12: what the verifier refuses of function values, one rule each.</summary>
  [Test]
  public void Verifier_RefusesEveryBrokenRule() {
    var x = new IrLocal("x", IrType.I32);
    var y = new IrLocal("y", IrType.I32);
    var f = new IrLocal("f", _unary);
    void Refused(string message, params IrStmt[] statements) {
      var problems = Problems(new IrModule("M", [F("F", IrType.Void, [x], [f, y], statements), new IrFunction("H", false, IrType.I32, [y], [], new IrBlock([new IrReturn(R(y))]))]));
      Assert.That(problems, Has.Some.Contains(message), string.Join("\n", problems));
    }

    IrStmt Use(IrExpr value) => new IrExprStmt(value);
    var reads = new IrBlock([new IrReturn(R(x))]);
    Refused("'x' is used outside the scope", Use(new IrLambda(_ints, [], [], [], reads)));
    Refused("captures 'y', which is not in scope", Use(new IrLambda(_ints, [y], [], [], new IrBlock([new IrReturn(R(y))]))));
    Refused("captures 'y', which it declares itself", new IrDeclare(y, I(0)), Use(new IrLambda(_unary, [y], [y], [], new IrBlock([new IrReturn(R(y))]))));
    Refused("captures a variable twice", Use(new IrLambda(_ints, [x, x], [], [], reads)));
    Refused("a lambda of the type i32", Use(new IrLambda(IrType.I32, [], [], [], reads)));
    Refused("takes (i32)", Use(new IrLambda(_ints, [], [y], [], new IrBlock([new IrReturn(R(y))]))));
    Refused("a break outside a loop", new IrWhile(new IrConst(IrType.Bool, 1), new IrBlock([Use(new IrLambda(IrType.Function(IrType.Void), [], [], [], new IrBlock([new IrBreak()]))), new IrBreak()])));
    Refused("the value returned has the type i64", Use(new IrLambda(_ints, [], [], [], new IrBlock([new IrReturn(new IrConst(IrType.I64, 1))]))));
    Refused("called with 0 arguments", new IrDeclare(f, new IrNull(_unary)), Use(new IrInvoke(IrType.I32, R(f), [])));
    Refused("called with (i64)", new IrDeclare(f, new IrNull(_unary)), Use(new IrInvoke(IrType.I32, R(f), [new IrConst(IrType.I64, 1)])));
    Refused("has the type i64", new IrDeclare(f, new IrNull(_unary)), Use(new IrInvoke(IrType.I64, R(f), [I(1)])));
    Refused("a value of the type i32 is called", Use(new IrInvoke(IrType.I32, R(x), [])));
    Refused("the function 'G' used as a value does not exist", Use(new IrFunctionRef(_unary, "G")));
    Refused("the function 'H' is used as a value of the type fn() -> i32", Use(new IrFunctionRef(_ints, "H")));
    Refused("the function 'H' is used as a value of the type i32", Use(new IrFunctionRef(IrType.I32, "H")));
    Refused("a for lists a variable of every pass twice", new IrFor(new IrConst(IrType.Bool, 0), new IrBlock([]), new IrBlock([])) { PerIteration = [x, x] });
    Refused("'y' is used outside the scope", new IrFor(new IrConst(IrType.Bool, 0), new IrBlock([]), new IrBlock([])) { PerIteration = [y] });
    Refused("a function type takes the type span<i32>", Use(new IrNull(IrType.Function(IrType.I32, IrType.Span(IrType.I32)))));
    Refused("a function type returns the type span<i32>", Use(new IrNull(IrType.Function(IrType.Span(IrType.I32)))));
    Refused("a reference to an object is expected", Use(new IrNull(IrType.I32)));
    Refused("local 'z' of a lambda has the type void", Use(new IrLambda(IrType.Function(IrType.Void), [], [], [new IrLocal("z", IrType.Void)], new IrBlock([]))));
    Refused("parameter 'z' of a lambda has the type span<i32>", Use(new IrLambda(IrType.Function(IrType.Void, IrType.Span(IrType.I32)), [], [new IrLocal("z", IrType.Span(IrType.I32))], [], new IrBlock([]))));
    var twice = new IrLocal("z", IrType.I32);
    Refused("parameter 'z' of a lambda is declared twice", Use(new IrLambda(IrType.Function(IrType.Void, IrType.I32, IrType.I32), [], [twice, twice], [], new IrBlock([]))));
  }

  /// <summary>What the verifier refuses of a method as a value.</summary>
  [Test]
  public void Verifier_RefusesBrokenBoundMethods() {
    var type = new IrClassType("K");
    var constructor = new IrConstructor("K.ctor");
    var get = new IrMethod("Get", "K.Get", false);
    var make = new IrMethod("Make", "K.Make", true);
    var stray = new IrMethod("Stray", "K.Stray", false);
    type.Define([]);
    type.DefineMembers([constructor], [get, make], [], []);
    var self = new IrLocal("this", IrType.Object(type));
    var reader = new IrLocal("this", IrType.Object(type));
    var k = new IrLocal("k", IrType.Object(type));
    void Refused(string message, IrExpr value) {
      var module = new IrModule("M", [
        new("K.ctor", false, IrType.Void, [self], [], new IrBlock([])),
        new("K.Get", false, IrType.I32, [reader], [], new IrBlock([new IrReturn(I(1))])),
        new("K.Make", false, IrType.I32, [], [], new IrBlock([new IrReturn(I(5))])),
        F("F", IrType.Void, [], [k], new IrDeclare(k, new IrNull(IrType.Object(type))), new IrExprStmt(value))
      ]) { Types = [type] };
      Assert.That(Problems(module), Has.Some.Contains(message), string.Join("\n", Problems(module)));
    }

    Refused("the method 'Get' is bound without a receiver", new IrBoundMethod(_ints, null, get));
    Refused("the method 'Make' is bound with a receiver", new IrBoundMethod(_ints, R(k), make));
    Refused("the method 'Get' is bound as a value of the type fn() -> i64", new IrBoundMethod(IrType.Function(IrType.I64), R(k), get));
    Refused("the method 'Get' is bound as a Virtual call", new IrBoundMethod(_ints, R(k), get) { Kind = IrMethodCallKind.Virtual });
    Refused("the method 'Get' is bound as a Base call", new IrBoundMethod(_ints, R(k), get) { Kind = IrMethodCallKind.Base });
    Refused("the method 'Stray' bound as a value is not one of a class", new IrBoundMethod(_ints, R(k), stray));
    Refused("the method 'Get' of K is bound to the type i32", new IrBoundMethod(_ints, I(1), get));
    Refused("the function 'K.Get' of a member is used as a value", new IrFunctionRef(_ints, "K.Get"));
  }

  /// <summary>The passes that rebuild a function keep the per-iteration variables of a loop and rebuild what a lambda holds.</summary>
  [Test]
  public void Rewriters_KeepFunctionValues() {
    var i = new IrLocal("i", IrType.I32);
    var p = new IrLocal("p", IrType.I32);
    var lambda = IrLambda.Of([p], IrType.I32, [], new IrBlock([new IrReturn(Add(R(p), R(i)))]));
    var loop = new IrFor(new IrBinary(IrBinaryOp.Less, R(i), I(1)), new IrBlock([new IrExprStmt(new IrInvoke(IrType.I32, lambda, [I(1)]))]), new IrBlock([new IrAssign(i, I(1))])) { PerIteration = [i] };
    var function = F("F", IrType.Void, [], [i], new IrDeclare(i, I(0)), loop);
    var renamed = IrGenerics.Rename(new IrModule("M", [function]), new Dictionary<string, string> { ["F"] = "G" });
    Assert.That(Problems(renamed), Is.Empty);
    var rebuilt = (IrFor)renamed.Functions[0].Body.Statements[1];
    Assert.That(rebuilt.PerIteration, Is.EqualTo(new[] { i }));
    Assert.That(IrTree.Nodes(function.Body).OfType<IrLocalRef>().Count(reference => reference.Local == p), Is.EqualTo(1), "the nodes of a function include those of its lambdas");
    Assert.That(IrClosures.Captured(function.Body), Is.EquivalentTo(new[] { i }));
  }

}
