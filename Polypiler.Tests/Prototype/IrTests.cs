using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Prototype;

/// <summary>The verifier and the interpreter on IR built by hand, including IR no frontend would produce.</summary>
[TestFixture]
public sealed class IrTests {

  private static readonly IrType _ints = IrType.Array(IrType.I32);
  private static readonly IrLocal _a = new("a", IrType.I32);
  private static readonly IrLocal _u = new("u", IrType.U32);
  private static readonly IrLocal _flag = new("flag", IrType.Bool);
  private static readonly IrLocal _data = new("data", _ints);
  private static readonly IrLocal _x = new("x", IrType.I32);
  private static readonly IrExpr _one = new IrConst(IrType.I32, 1);
  private static readonly IrExpr _true = new IrConst(IrType.Bool, 1);

  private sealed record Mystery() : IrExpr(IrType.I32);

  private sealed record Riddle : IrStmt;

  private static IrFunction Function(string name, IrType returns, IrLocal[] parameters, IrLocal[] locals, params IrStmt[] body)
    => new(name, true, returns, parameters, locals, new IrBlock(body));

  /// <summary>A module with one function <c>F(a, u, flag, data)</c> that declares <c>x</c>, and a helper <c>Twice(a)</c>.</summary>
  private static IrModule Module(IrType returns, params IrStmt[] body) => new("M", [
    Function("F", returns, [_a, _u, _flag, _data], [_x], body),
    Function("Twice", IrType.I32, [_a], [], new IrReturn(new IrBinary(IrBinaryOp.Add, new IrLocalRef(_a), new IrLocalRef(_a)))),
    Function("Nothing", IrType.Void, [], [])
  ]);

  private static IrModule Returning(IrExpr value) => Module(value.Type, new IrReturn(value));

  private static IrExpr Ref(IrLocal local) => new IrLocalRef(local);

  private static IEnumerable<TestCaseData> IllTyped() {
    TestCaseData Case(string name, IrModule module, string message) => new TestCaseData(module, message).SetName("IllTyped: " + name);
    IrModule Statement(params IrStmt[] body) => Module(IrType.Void, body);
    IrExpr nothing = new IrCall("Nothing", IrType.Void, []);

    yield return Case("i32 added to bool", Returning(new IrBinary(IrBinaryOp.Add, _one, _true)), "Add is applied to i32 and bool");
    yield return Case("function declared twice", new IrModule("M", [Function("F", IrType.Void, [], []), Function("F", IrType.Void, [], [])]), "declares the function twice");
    yield return Case("return type", new IrModule("M", [Function("F", IrType.Array(IrType.Bool), [], [])]), "the return type bool[]");
    yield return Case("void parameter", new IrModule("M", [Function("F", IrType.Void, [new IrLocal("p", IrType.Void)], [])]), "parameter 'p' has the type void");
    yield return Case("parameter twice", new IrModule("M", [Function("F", IrType.Void, [_a, _a], [])]), "parameter 'a' is declared twice");
    yield return Case("local of arrays of arrays", new IrModule("M", [Function("F", IrType.Void, [], [new IrLocal("l", IrType.Array(_ints))])]), "local 'l' has the type i32[][]");
    yield return Case("unlisted local", Statement(new IrDeclare(new IrLocal("y", IrType.I32), null)), "local 'y' is not among the locals");
    yield return Case("local twice", Statement(new IrDeclare(_x, null), new IrDeclare(_x, _one)), "local 'x' is declared twice");
    yield return Case("initializer type", Statement(new IrDeclare(_x, _true)), "the initializer of 'x' has the type bool, expected i32");
    yield return Case("assignment out of scope", Statement(new IrBlock([new IrDeclare(_x, _one)]), new IrAssign(_x, _one)), "'x' is used outside the scope");
    yield return Case("assignment type", Statement(new IrAssign(_a, _true)), "the value assigned to 'a' has the type bool, expected i32");
    yield return Case("store into a number", Statement(new IrStore(Ref(_a), _one, _one)), "an element is taken of the type i32");
    yield return Case("store at a truth value", Statement(new IrStore(Ref(_data), _true, _one)), "an array index has the type bool");
    yield return Case("store of the wrong type", Statement(new IrStore(Ref(_data), _one, Ref(_u))), "the value stored in an array element has the type u32, expected i32");
    yield return Case("if on a number", Statement(new IrIf(_one, new IrBlock([]), null)), "the condition of an if has the type i32");
    yield return Case("else is checked", Statement(new IrIf(_true, new IrBlock([]), new IrBlock([new IrAssign(_a, _true)]))), "the value assigned to 'a'");
    yield return Case("while on a number", Statement(new IrWhile(_one, new IrBlock([]))), "the condition of a while has the type i32");
    yield return Case("while body is checked", Statement(new IrWhile(_true, new IrBlock([new IrAssign(_a, _true)]))), "the value assigned to 'a'");
    yield return Case("return without value", Module(IrType.I32, new IrReturn(null)), "a return without a value");
    yield return Case("return of the wrong type", Module(IrType.I32, new IrReturn(_true)), "the value returned has the type bool, expected i32");
    yield return Case("return of a value from void", Statement(new IrReturn(_one)), "the value returned has the type i32, expected void");
    yield return Case("unknown statement", Statement(new Riddle()), "unknown statement Riddle");
    yield return Case("unknown expression", Returning(new Mystery()), "unknown expression Mystery");
    yield return Case("byte constant too large", Returning(new IrConst(IrType.U8, 256)), "the constant 256 is not a value of u8");
    yield return Case("byte constant negative", Returning(new IrConst(IrType.U8, -1)), "the constant -1 is not a value of u8");
    yield return Case("i32 constant too large", Returning(new IrConst(IrType.I32, 1L << 31)), "is not a value of i32");
    yield return Case("i32 constant too small", Returning(new IrConst(IrType.I32, -(1L << 31) - 1)), "is not a value of i32");
    yield return Case("u32 constant negative", Returning(new IrConst(IrType.U32, -1)), "is not a value of u32");
    yield return Case("u32 constant too large", Returning(new IrConst(IrType.U32, 1L << 32)), "is not a value of u32");
    yield return Case("truth value 2", Returning(new IrConst(IrType.Bool, 2)), "the constant 2 is not a value of bool");
    yield return Case("array constant", Returning(new IrConst(_ints, 0)), "the constant 0 is not a value of i32[]");
    yield return Case("read out of scope", Returning(Ref(_x)), "'x' is used outside the scope");
    yield return Case("negated u32", Returning(new IrUnary(IrUnaryOp.Negate, Ref(_u))), "Negate is applied to u32");
    yield return Case("complement of a truth value", Returning(new IrUnary(IrUnaryOp.BitNot, _true)), "BitNot is applied to bool");
    yield return Case("logical not of a number", Returning(new IrUnary(IrUnaryOp.LogicalNot, _one)), "LogicalNot is applied to i32");
    yield return Case("shift by u32", Returning(new IrBinary(IrBinaryOp.ShiftLeft, _one, Ref(_u))), "ShiftLeft is applied to i32 and u32");
    yield return Case("shift of a truth value", Returning(new IrBinary(IrBinaryOp.ShiftRight, _true, _one)), "ShiftRight is applied to bool and i32");
    yield return Case("equality of arrays", Returning(new IrBinary(IrBinaryOp.Equal, Ref(_data), Ref(_data))), "Equal is applied to i32[] and i32[]");
    yield return Case("equality of mixed types", Returning(new IrBinary(IrBinaryOp.NotEqual, _true, _one)), "NotEqual is applied to bool and i32");
    yield return Case("and of numbers", Returning(new IrBinary(IrBinaryOp.LogicalAnd, _one, _one)), "LogicalAnd is applied to i32 and i32");
    yield return Case("or with a number", Returning(new IrBinary(IrBinaryOp.LogicalOr, _true, _one)), "LogicalOr is applied to bool and i32");
    yield return Case("order of truth values", Returning(new IrBinary(IrBinaryOp.Less, _true, _true)), "Less is applied to bool and bool");
    yield return Case("i32 with u32", Returning(new IrBinary(IrBinaryOp.Multiply, _one, Ref(_u))), "Multiply is applied to i32 and u32");
    yield return Case("conversion from bool", Returning(new IrConvert(IrType.I32, _true)), "a conversion from bool to i32");
    yield return Case("conversion to bool", Returning(new IrConvert(IrType.Bool, _one)), "a conversion from i32 to bool");
    yield return Case("conditional on a number", Returning(new IrConditional(_one, _one, _one)), "the condition of a conditional expression has the type i32");
    yield return Case("conditional branches", Returning(new IrConditional(_true, _one, Ref(_u))), "the second branch of a conditional expression has the type u32, expected i32");
    yield return Case("conditional of nothing", Statement(new IrExprStmt(new IrConditional(_true, nothing, nothing))), "a conditional expression of the type void");
    yield return Case("element of a number", Returning(new IrArrayLoad(Ref(_a), _one)), "an element is taken of the type i32");
    yield return Case("element at a truth value", Returning(new IrArrayLoad(Ref(_data), _true)), "an array index has the type bool");
    yield return Case("length of a number", Returning(new IrArrayLength(Ref(_a))), "the length is taken of the type i32");
    yield return Case("new number", Returning(new IrNewArray(IrType.I32, _one)), "an array is created of the type i32");
    yield return Case("new array of truth values", Returning(new IrNewArray(IrType.Array(IrType.Bool), _one)), "an array is created of the type bool[]");
    yield return Case("array length of a truth value", Returning(new IrNewArray(_ints, _true)), "an array length has the type bool");
    yield return Case("call of nothing", Returning(new IrCall("Missing", IrType.I32, [])), "the called function 'Missing' does not exist");
    yield return Case("call type", Returning(new IrCall("Twice", IrType.U32, [_one])), "the call of 'Twice' has the type u32, the function returns i32");
    yield return Case("argument count", Returning(new IrCall("Twice", IrType.I32, [])), "the arguments of the call of 'Twice' do not match");
    yield return Case("argument type", Returning(new IrCall("Twice", IrType.I32, [_true])), "the arguments of the call of 'Twice' do not match");
    yield return Case("argument is checked", Returning(new IrCall("Twice", IrType.I32, [Ref(_x)])), "'x' is used outside the scope");
  }

  /// <summary>Test design 14: ill-typed IR is reported by the verifier and refused by the emitter.</summary>
  [TestCaseSource(nameof(IllTyped))]
  public void IllTypedIr_IsReportedAndNotEmitted(IrModule module, string message) {
    var diagnostics = IrVerifier.Verify(module);
    Assert.That(diagnostics.Select(diagnostic => diagnostic.Message), Has.Some.Contains(message));
    Assert.That(diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Severity, diagnostic.File)),
      Has.All.EqualTo(("PLY0100", DiagnosticSeverity.Error, (string?)null)));
    Assert.That(diagnostics[0].Message, Does.StartWith("ill-typed IR in '"));
    Assert.That(() => JavaScriptEmitter.Emit(module), Throws.ArgumentException.With.Message.Contains("cannot be emitted"));
  }

  [Test]
  public void WellTypedIr_IsAccepted() {
    var module = Module(IrType.I32,
      new IrDeclare(_x, null),
      new IrAssign(_x, new IrCall("Twice", IrType.I32, [Ref(_a)])),
      new IrStore(Ref(_data), Ref(_u), Ref(_x)),
      new IrExprStmt(new IrCall("Nothing", IrType.Void, [])),
      new IrIf(Ref(_flag), new IrBlock([new IrReturn(new IrArrayLength(new IrNewArray(_ints, Ref(_u))))]), new IrBlock([])),
      new IrReturn(new IrConditional(new IrBinary(IrBinaryOp.Equal, Ref(_flag), _true), Ref(_x), new IrUnary(IrUnaryOp.Negate, _one))));
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }

  /// <summary>A local declared in a block that ended can be declared again in the next one.</summary>
  [Test]
  public void SiblingBlocks_MayDeclareTheSameLocal() {
    var block = new IrBlock([new IrDeclare(_x, _one), new IrAssign(_x, _one)]);
    Assert.That(IrVerifier.Verify(Module(IrType.Void, block, block)), Is.Empty);
  }

  [Test]
  public void Types_HaveTheirNamesAndClasses() {
    Assert.That(new[] { IrType.Void, IrType.Bool, IrType.U8, IrType.I32, IrType.U32, _ints }.Select(type => type.ToString()),
      Is.EqualTo(new[] { "void", "bool", "u8", "i32", "u32", "i32[]" }));
    Assert.That(new[] { IrType.Void, IrType.Bool, IrType.U8, IrType.I32, IrType.U32, _ints }.Select(type => (type.IsInteger, type.IsWord, type.IsArray, type.IsStorable)),
      Is.EqualTo(new[] { (false, false, false, false), (false, false, false, true), (true, false, false, true), (true, true, false, true), (true, true, false, true), (false, false, true, true) }));
    var bytes = IrType.Array(IrType.U8);
    Assert.That(IrType.Array(IrType.U8), Is.EqualTo(bytes).And.Not.EqualTo(_ints));
    Assert.That(_ints.Element, Is.EqualTo(IrType.I32));
    Assert.That(IrType.Array(IrType.Bool).IsStorable, Is.False);
    Assert.That(new IrArrayLoad(_one, _one).Type, Is.EqualTo(IrType.Void));
  }

  /// <summary>Test design 4: a value converted to a narrower type is its low bits.</summary>
  [TestCase(IrTypeKind.U8, 0x1FF, 255)]
  [TestCase(IrTypeKind.U8, -1, 255)]
  [TestCase(IrTypeKind.U32, -1, 4294967295)]
  [TestCase(IrTypeKind.I32, 4294967295, -1)]
  [TestCase(IrTypeKind.I32, 0x80000000, -2147483648)]
  public void Wrap_KeepsTheLowBits(IrTypeKind kind, long value, long expected) {
    var type = kind switch { IrTypeKind.U8 => IrType.U8, IrTypeKind.U32 => IrType.U32, _ => IrType.I32 };
    Assert.That(IrOperators.Wrap(type, value), Is.EqualTo(expected));
  }

  [TestCase(IrTrap.DivideByZero, "divide by zero")]
  [TestCase(IrTrap.IndexOutOfRange, "index out of range")]
  [TestCase(IrTrap.IntegerOverflow, "integer overflow")]
  public void Trap_HasItsMessage(IrTrap trap, string message) {
    var exception = new IrTrapException(trap);
    Assert.That((exception.Trap, exception.Message, IrTrapException.Describe(trap)), Is.EqualTo((trap, message, message)));
  }

  private static object? Run(IrType returns, object[] arguments, params IrStmt[] body)
    => IrInterpreter.Call(Module(returns, body), "F", arguments);

  private static object[] Arguments(int a = 3, uint u = 4, bool flag = true, int[]? data = null) => [a, u, flag, data ?? [10, 20]];

  [Test]
  public void Interpreter_ReturnsEachTypeAcrossTheBoundary() {
    var data = new[] { 10, 20 };
    Assert.That(Run(IrType.I32, Arguments(), new IrReturn(Ref(_a))), Is.EqualTo(3).And.TypeOf<int>());
    Assert.That(Run(IrType.U32, Arguments(), new IrReturn(Ref(_u))), Is.EqualTo(4u).And.TypeOf<uint>());
    Assert.That(Run(IrType.U8, Arguments(), new IrReturn(new IrConvert(IrType.U8, Ref(_a)))), Is.EqualTo((byte)3).And.TypeOf<byte>());
    Assert.That(Run(IrType.Bool, Arguments(), new IrReturn(Ref(_flag))), Is.True);
    Assert.That(Run(_ints, Arguments(data: data), new IrReturn(Ref(_data))), Is.SameAs(data));
    Assert.That(Run(IrType.Void, Arguments(), new IrReturn(null)), Is.Null);
    Assert.That(Run(IrType.Void, Arguments()), Is.Null);
    Assert.That(Run(IrType.I32, Arguments(), new IrDeclare(_x, null), new IrAssign(_x, _one), new IrReturn(Ref(_x))), Is.EqualTo(1));
  }

  [Test]
  public void Interpreter_CreatesArraysOfEachElementType() {
    object? Create(IrType element, int length) => IrInterpreter.Call(
      new IrModule("M", [Function("New", IrType.Array(element), [_a], [], new IrReturn(new IrNewArray(IrType.Array(element), Ref(_a))))]), "New", [length]);

    Assert.That(Create(IrType.I32, 2), Is.EqualTo(new int[2]).And.TypeOf<int[]>());
    Assert.That(Create(IrType.U32, 1), Is.EqualTo(new uint[1]).And.TypeOf<uint[]>());
    Assert.That(Create(IrType.U8, 0), Is.Empty.And.TypeOf<byte[]>());
    Assert.That(() => Create(IrType.U8, -1), Throws.TypeOf<IrTrapException>().With.Property(nameof(IrTrapException.Trap)).EqualTo(IrTrap.IntegerOverflow));
  }

  /// <summary>What the caller hands in has to fit the function.</summary>
  [Test]
  public void Interpreter_RefusesCallsThatDoNotFit() {
    var module = Module(IrType.I32, new IrReturn(Ref(_a)));
    Assert.That(() => IrInterpreter.Call(module, "Missing", []), Throws.ArgumentException.With.Message.Contains("has no function 'Missing'"));
    Assert.That(() => IrInterpreter.Call(module, "F", [1]), Throws.ArgumentException.With.Message.Contains("takes 4 arguments, not 1"));
    Assert.That(() => IrInterpreter.Call(module, "F", ["3", 4u, true, new int[0]]), Throws.ArgumentException.With.Message.Contains("'a' is not a value of i32"));
    Assert.That(() => IrInterpreter.Call(module, "F", [3, 4, true, new int[0]]), Throws.ArgumentException.With.Message.Contains("'u' is not a value of u32"));
    Assert.That(() => IrInterpreter.Call(module, "F", [3, 4u, 1, new int[0]]), Throws.ArgumentException.With.Message.Contains("'flag' is not a value of bool"));
    Assert.That(() => IrInterpreter.Call(module, "F", [3, 4u, true, new uint[0]]), Throws.ArgumentException.With.Message.Contains("'data' is not a value of i32[]"));
    Assert.That(() => IrInterpreter.Call(module, "F", [3, 4u, true, null!]), Throws.ArgumentException.With.Message.Contains("'data' is not a value of i32[]"));
  }

  /// <summary>The interpreter does not make up a result for IR it does not know or that never returns one.</summary>
  [Test]
  public void Interpreter_StopsOnIrItCannotRun() {
    Assert.That(() => Run(IrType.I32, Arguments()), Throws.InvalidOperationException.With.Message.Contains("'F' ended without returning a value"));
    Assert.That(() => Run(IrType.I32, Arguments(), new IrIf(new IrConst(IrType.Bool, 0), new IrBlock([new IrReturn(_one)]), null)), Throws.InvalidOperationException);
    Assert.That(() => Run(IrType.Void, Arguments(), new Riddle()), Throws.TypeOf<NotSupportedException>().With.Message.Contains("Riddle"));
    Assert.That(() => Run(IrType.I32, Arguments(), new IrReturn(new Mystery())), Throws.TypeOf<NotSupportedException>().With.Message.Contains("Mystery"));
  }

}
