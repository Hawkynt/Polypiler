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
    IrExpr big = new IrConst(IrType.I64, 1);
    IrExpr small = new IrConst(IrType.U8, 1);
    yield return Case("checked negation of u32", Returning(new IrUnary(IrUnaryOp.NegateChecked, Ref(_u))), "NegateChecked is applied to u32");
    yield return Case("negated u64", Returning(new IrUnary(IrUnaryOp.Negate, new IrConst(IrType.U64, 1))), "Negate is applied to u64");
    yield return Case("complement of a byte", Returning(new IrUnary(IrUnaryOp.BitNot, small)), "BitNot is applied to u8");
    yield return Case("checked sum of truth values", Returning(new IrBinary(IrBinaryOp.AddChecked, _true, _true)), "AddChecked is applied to bool and bool");
    yield return Case("checked product of mixed widths", Returning(new IrBinary(IrBinaryOp.MultiplyChecked, _one, big)), "MultiplyChecked is applied to i32 and i64");
    yield return Case("checked difference of bytes", Returning(new IrBinary(IrBinaryOp.SubtractChecked, small, small)), "SubtractChecked is applied to u8 and u8");
    yield return Case("sum of bytes", Returning(new IrBinary(IrBinaryOp.Add, small, small)), "Add is applied to u8 and u8");
    yield return Case("i64 with i32", Returning(new IrBinary(IrBinaryOp.Subtract, big, _one)), "Subtract is applied to i64 and i32");
    yield return Case("shift by 64 bits", Returning(new IrBinary(IrBinaryOp.ShiftLeft, big, big)), "ShiftLeft is applied to i64 and i64");
    yield return Case("comparison of narrow integers", Returning(new IrBinary(IrBinaryOp.Less, small, small)), "Less is applied to u8 and u8");
    yield return Case("i8 constant too large", Returning(new IrConst(IrType.I8, 128)), "the constant 128 is not a value of i8");
    yield return Case("i16 constant too small", Returning(new IrConst(IrType.I16, -32769)), "the constant -32769 is not a value of i16");
    yield return Case("u16 constant too large", Returning(new IrConst(IrType.U16, 65536)), "the constant 65536 is not a value of u16");
    yield return Case("void constant", Statement(new IrExprStmt(new IrConst(IrType.Void, 0))), "the constant 0 is not a value of void");
    IrExpr real = new IrConst(IrType.F64, 0);
    IrExpr single = new IrConst(IrType.F32, 0);
    yield return Case("complement of a float", Returning(new IrUnary(IrUnaryOp.BitNot, real)), "BitNot is applied to f64");
    yield return Case("checked negation of a float", Returning(new IrUnary(IrUnaryOp.NegateChecked, single)), "NegateChecked is applied to f32");
    yield return Case("checked sum of floats", Returning(new IrBinary(IrBinaryOp.AddChecked, real, real)), "AddChecked is applied to f64 and f64");
    yield return Case("and of floats", Returning(new IrBinary(IrBinaryOp.And, single, single)), "And is applied to f32 and f32");
    yield return Case("shift of a float", Returning(new IrBinary(IrBinaryOp.ShiftLeft, real, _one)), "ShiftLeft is applied to f64 and i32");
    yield return Case("f32 with f64", Returning(new IrBinary(IrBinaryOp.Divide, single, real)), "Divide is applied to f32 and f64");
    yield return Case("float compared with an integer", Returning(new IrBinary(IrBinaryOp.Less, real, _one)), "Less is applied to f64 and i32");
    yield return Case("f32 constant with 64 bits", Returning(new IrConst(IrType.F32, 1L << 32)), "the constant 4294967296 is not a value of f32");
    yield return Case("float to integer that wraps", Returning(new IrConvert(IrType.I32, real)), "a conversion from f64 to i32 with the contract Wrap");
    yield return Case("integer to integer that saturates", Returning(new IrConvert(IrType.U8, _one, IrConversion.Saturate)), "a conversion from i32 to u8 with the contract Saturate");
    yield return Case("integer to float that traps", Returning(new IrConvert(IrType.F64, _one, IrConversion.Trap)), "a conversion from i32 to f64 with the contract Trap");
    yield return Case("float to float that saturates", Returning(new IrConvert(IrType.F32, real, IrConversion.Saturate)), "a conversion from f64 to f32 with the contract Saturate");
    yield return Case("float to bool", Returning(new IrConvert(IrType.Bool, real, IrConversion.Saturate)), "a conversion from f64 to bool");
    yield return Case("array to float", Returning(new IrConvert(IrType.F64, Ref(_data))), "a conversion from i32[] to f64");
    IrSwitchSection Section(long[] labels, bool isDefault, params IrStmt[] statements) => new(labels, isDefault, new IrBlock(statements));
    var wrong = new IrAssign(_a, _true);
    var empty = new IrBlock([]);
    yield return Case("break outside", Statement(new IrBreak()), "a break outside a loop and outside a switch");
    yield return Case("break after its loop", Statement(new IrWhile(_true, empty), new IrBreak()), "a break outside a loop and outside a switch");
    yield return Case("continue outside", Statement(new IrContinue()), "a continue outside a loop");
    yield return Case("continue in a switch only", Statement(new IrSwitch(_one, [Section([], true, new IrContinue())])), "a continue outside a loop");
    yield return Case("do on a number", Statement(new IrDoWhile(empty, _one)), "the condition of a do has the type i32");
    yield return Case("do body is checked", Statement(new IrDoWhile(new IrBlock([wrong]), _true)), "the value assigned to 'a'");
    yield return Case("for on a number", Statement(new IrFor(_one, empty, empty)), "the condition of a for has the type i32");
    yield return Case("for body is checked", Statement(new IrFor(_true, new IrBlock([wrong]), empty)), "the value assigned to 'a'");
    yield return Case("for step is checked", Statement(new IrFor(_true, empty, new IrBlock([wrong]))), "the value assigned to 'a'");
    yield return Case("for step that branches", Statement(new IrFor(_true, empty, new IrBlock([new IrIf(_true, empty, null)]))), "the step of a for holds a IrIf");
    yield return Case("for step that jumps", Statement(new IrFor(_true, empty, new IrBlock([new IrBreak()]))), "the step of a for holds a IrBreak");
    yield return Case("switch on a truth value", Statement(new IrSwitch(_true, [])), "a switch on the type bool");
    yield return Case("switch on a float", Statement(new IrSwitch(new IrConst(IrType.F64, 0), [Section([0], false)])), "a switch on the type f64");
    yield return Case("switch value is checked", Statement(new IrSwitch(Ref(_x), [])), "'x' is used outside the scope");
    yield return Case("label outside the type", Statement(new IrSwitch(new IrConst(IrType.U8, 1), [Section([256], false)])), "the label 256 is not a value of u8");
    yield return Case("label twice", Statement(new IrSwitch(_one, [Section([1, 2], false), Section([2], false)])), "the label 2 stands twice in a switch");
    yield return Case("two defaults", Statement(new IrSwitch(_one, [Section([], true), Section([1], true)])), "a switch with more than one default section");
    yield return Case("section without a label", Statement(new IrSwitch(_one, [Section([], false)])), "a switch section without a label");
    yield return Case("section body is checked", Statement(new IrSwitch(_one, [Section([1], false, wrong)])), "the value assigned to 'a'");
    yield return Case("step of a constant", Returning(new IrStep(_one, true, false, false)), "a step of a IrConst");
    yield return Case("step of a truth value", Returning(new IrStep(Ref(_flag), true, false, false)), "a step of the type bool");
    yield return Case("trapping step of a float", Statement(new IrExprStmt(new IrStep(new IrArrayLoad(new IrNewArray(IrType.Array(IrType.F64), _one), _one), true, true, true))), "a trapping step of the type f64");
    yield return Case("step out of scope", Returning(new IrStep(Ref(_x), false, true, true)), "'x' is used outside the scope");
    yield return Case("step of an element of a number", Returning(new IrStep(new IrArrayLoad(Ref(_a), _one), true, true, false)), "an element is taken of the type i32");
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
    IrExpr large = new IrConst(IrType.U64, -1);
    IrExpr wide = new IrConvert(IrType.I64, large, IrConversion.Trap);
    Assert.That(IrVerifier.Verify(Returning(new IrConvert(IrType.I8, new IrBinary(IrBinaryOp.ShiftRightLogical,
      new IrBinary(IrBinaryOp.MultiplyChecked, new IrUnary(IrUnaryOp.NegateChecked, wide), new IrUnary(IrUnaryOp.BitNot, wide)), _one)))), Is.Empty);
    Assert.That(IrVerifier.Verify(Returning(new IrBinary(IrBinaryOp.GreaterOrEqual, large, new IrConst(IrType.U64, long.MinValue)))), Is.Empty);
    IrExpr real = new IrConvert(IrType.F64, new IrConvert(IrType.F32, large));
    Assert.That(IrVerifier.Verify(Returning(new IrConvert(IrType.U8, new IrBinary(IrBinaryOp.Remainder, new IrUnary(IrUnaryOp.Negate, real), real), IrConversion.Saturate))), Is.Empty);
    Assert.That(IrVerifier.Verify(Returning(new IrConvert(IrType.I64, new IrConst(IrType.F32, int.MinValue), IrConversion.Trap))), Is.Empty);
    Assert.That(IrVerifier.Verify(Returning(new IrBinary(IrBinaryOp.NotEqual, real, real))), Is.Empty);
  }

  /// <summary>
  /// Every control-flow form on IR built by hand, including what no frontend builds: a section that
  /// ends without a jump, a step as a statement, a break inside a switch inside a loop.
  /// </summary>
  [Test]
  public void ControlFlow_IsAcceptedAndRuns() {
    IrSwitchSection Section(long[] labels, bool isDefault, params IrStmt[] statements) => new(labels, isDefault, new IrBlock(statements));
    IrExpr x = Ref(_x);
    IrExpr Const(long value) => new IrConst(IrType.I32, value);
    IrStmt Add(long value) => new IrAssign(_x, new IrBinary(IrBinaryOp.Add, x, Const(value)));
    var counter = new IrLocal("i", IrType.I32);
    var module = new IrModule("M", [Function("F", IrType.I32, [_a], [_x, counter],
      new IrDeclare(_x, Const(0)),
      new IrDeclare(counter, Const(0)),
      // for (i = 0; i < a; i++): 0 is skipped, 1 adds 10 and leaves the switch, 2 falls out of its section, 5 ends the loop.
      new IrFor(new IrBinary(IrBinaryOp.Less, Ref(counter), Ref(_a)), new IrBlock([
        new IrSwitch(Ref(counter), [
          Section([0], false, new IrContinue()),
          Section([1], false, Add(10), new IrBreak(), Add(1000000)),
          Section([2], false, Add(100)),
          Section([5], false, new IrBlock([new IrBreak()]))
        ]),
        new IrIf(new IrBinary(IrBinaryOp.Equal, Ref(counter), Const(5)), new IrBlock([new IrBreak()]), null),
        Add(1)
      ]), new IrBlock([new IrExprStmt(new IrStep(Ref(counter), true, true, false))])),
      new IrDoWhile(new IrBlock([Add(1000), new IrIf(new IrBinary(IrBinaryOp.Greater, x, Const(5000)), new IrBlock([new IrReturn(Const(-1))]), null)]),
        new IrBinary(IrBinaryOp.Less, x, Const(3000))),
      new IrReturn(x))]);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That(IrInterpreter.Call(module, "F", [0]), Is.EqualTo(3000));
    Assert.That(IrInterpreter.Call(module, "F", [1]), Is.EqualTo(3000));
    Assert.That(IrInterpreter.Call(module, "F", [2]), Is.EqualTo(3011));
    Assert.That(IrInterpreter.Call(module, "F", [3]), Is.EqualTo(3112));
    Assert.That(IrInterpreter.Call(module, "F", [5]), Is.EqualTo(3114));
    Assert.That(IrInterpreter.Call(module, "F", [9]), Is.EqualTo(3114));
    Assert.That(JavaScriptEmitter.Emit(module).Single().Content, Does.Contain("for (; i < a; ($p = i, i = ($p + 1) | 0, $p)) {"));
  }

  /// <summary>A return inside a loop, of each kind, leaves the function and not only the loop.</summary>
  [Test]
  public void Return_LeavesEveryKindOfLoop() {
    var leave = new IrBlock([new IrReturn(_one)]);
    var never = new IrReturn(new IrConst(IrType.I32, 2));
    Assert.That(Run(IrType.I32, Arguments(), new IrWhile(_true, leave), never), Is.EqualTo(1));
    Assert.That(Run(IrType.I32, Arguments(), new IrDoWhile(leave, _true), never), Is.EqualTo(1));
    Assert.That(Run(IrType.I32, Arguments(), new IrFor(_true, leave, new IrBlock([])), never), Is.EqualTo(1));
    Assert.That(Run(IrType.I32, Arguments(), new IrSwitch(_one, [new IrSwitchSection([1], false, leave)]), never), Is.EqualTo(1));
    Assert.That(Run(IrType.I32, Arguments(), new IrSwitch(_one, [new IrSwitchSection([], true, leave)]), never), Is.EqualTo(1));
    Assert.That(Run(IrType.I32, Arguments(), new IrSwitch(_one, [new IrSwitchSection([3], false, leave)]), never), Is.EqualTo(2));
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
    var integers = new[] { IrType.I8, IrType.U8, IrType.I16, IrType.U16, IrType.I32, IrType.U32, IrType.I64, IrType.U64 };
    Assert.That(integers.Select(type => type.ToString()), Is.EqualTo(new[] { "i8", "u8", "i16", "u16", "i32", "u32", "i64", "u64" }));
    Assert.That(integers.Select(type => (type.Bits, type.IsSigned)),
      Is.EqualTo(new[] { (8, true), (8, false), (16, true), (16, false), (32, true), (32, false), (64, true), (64, false) }));
    Assert.That(integers.Select(type => (type.IsInteger, type.IsNumeric, type.IsStorable, IrType.Array(type).IsStorable)), Has.All.EqualTo((true, true, true, true)));
    Assert.That(integers.Select(type => (type.IsWord, type.IsLong, type.IsWideInteger, type.IsArithmetic)), Is.EqualTo(new[] {
      (false, false, false, false), (false, false, false, false), (false, false, false, false), (false, false, false, false),
      (true, false, true, true), (true, false, true, true), (false, true, true, true), (false, true, true, true)
    }));
    Assert.That(new[] { IrType.Void, IrType.Bool, _ints }.Select(type => (type.Bits, type.IsSigned, type.IsNumeric, type.IsArithmetic, type.IsLong)),
      Has.All.EqualTo((0, false, false, false, false)));
    Assert.That(new[] { IrType.F32, IrType.F64 }.Select(type => type.ToString()), Is.EqualTo(new[] { "f32", "f64" }));
    Assert.That(new[] { IrType.F32, IrType.F64 }.Select(type => (type.IsFloat, type.IsNumeric, type.IsArithmetic, type.IsStorable, IrType.Array(type).IsStorable)),
      Has.All.EqualTo((true, true, true, true, true)));
    Assert.That(new[] { IrType.F32, IrType.F64 }.Select(type => (type.IsInteger, type.IsWideInteger, type.IsSigned, type.Bits)), Has.All.EqualTo((false, false, false, 0)));
    Assert.That(integers.Select(type => type.IsFloat), Has.All.False);
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
  [TestCase(IrTypeKind.I8, 0x80, -128)]
  [TestCase(IrTypeKind.I8, 0x17F, 127)]
  [TestCase(IrTypeKind.I16, 0x18000, -32768)]
  [TestCase(IrTypeKind.U16, -1, 65535)]
  [TestCase(IrTypeKind.I64, long.MinValue, long.MinValue)]
  [TestCase(IrTypeKind.U64, -1, -1)]
  public void Wrap_KeepsTheLowBits(IrTypeKind kind, long value, long expected)
    => Assert.That(IrOperators.Wrap(Scalar(kind), value), Is.EqualTo(expected));

  private static IrType Scalar(IrTypeKind kind)
    => new[] { IrType.I8, IrType.U8, IrType.I16, IrType.U16, IrType.I32, IrType.U32, IrType.I64, IrType.U64 }.Single(type => type.Kind == kind);

  /// <summary>The range of every integer type, and which type holds every value of which.</summary>
  [TestCase(IrTypeKind.I8, "-128", "127")]
  [TestCase(IrTypeKind.U8, "0", "255")]
  [TestCase(IrTypeKind.I16, "-32768", "32767")]
  [TestCase(IrTypeKind.U16, "0", "65535")]
  [TestCase(IrTypeKind.I32, "-2147483648", "2147483647")]
  [TestCase(IrTypeKind.U32, "0", "4294967295")]
  [TestCase(IrTypeKind.I64, "-9223372036854775808", "9223372036854775807")]
  [TestCase(IrTypeKind.U64, "0", "18446744073709551615")]
  public void IntegerType_HasItsRange(IrTypeKind kind, string minimum, string maximum) {
    var type = Scalar(kind);
    Assert.That((IrOperators.Minimum(type).ToString(), IrOperators.Maximum(type).ToString()), Is.EqualTo((minimum, maximum)));
    Assert.That(IrOperators.Contains(type, type), Is.True);
    Assert.That(IrOperators.Contains(IrType.I64, type), Is.EqualTo(kind != IrTypeKind.U64));
    Assert.That(IrOperators.Contains(type, IrType.U8), Is.EqualTo(kind != IrTypeKind.I8));
    Assert.That(IrOperators.Number(type, -1).ToString(), Is.EqualTo(kind == IrTypeKind.U64 ? maximum : "-1"));
  }

  /// <summary>A floating-point constant is its bits; a binary32 one is rounded on the way in and exact on the way out.</summary>
  [Test]
  public void FloatConstant_IsCarriedAsItsBits() {
    Assert.That(IrOperators.Bits(IrType.F64, 1.5), Is.EqualTo(0x3FF8000000000000));
    Assert.That(IrOperators.Bits(IrType.F32, 1.5), Is.EqualTo(0x3FC00000));
    Assert.That(IrOperators.Bits(IrType.F32, 0.1), Is.EqualTo(BitConverter.SingleToInt32Bits(0.1f)));
    Assert.That(IrOperators.Bits(IrType.F32, -0.0), Is.EqualTo(int.MinValue));
    Assert.That(IrOperators.Float(IrType.F64, IrOperators.Bits(IrType.F64, -0.1)), Is.EqualTo(-0.1));
    Assert.That(IrOperators.Float(IrType.F32, IrOperators.Bits(IrType.F32, 0.1)), Is.EqualTo((double)0.1f));
    Assert.That(double.IsNaN(IrOperators.Float(IrType.F32, IrOperators.Bits(IrType.F32, double.NaN))), Is.True);
  }

  [Test]
  public void CheckedOperators_AreTheOnesThatTrap() {
    Assert.That(new[] { IrBinaryOp.AddChecked, IrBinaryOp.SubtractChecked, IrBinaryOp.MultiplyChecked }.Select(IrOperators.Traps), Has.All.True);
    Assert.That(new[] { IrBinaryOp.Add, IrBinaryOp.Divide, IrBinaryOp.LogicalOr }.Select(IrOperators.Traps), Has.All.False);
    Assert.That(new IrBinary(IrBinaryOp.MultiplyChecked, _one, _one).Type, Is.EqualTo(IrType.I32));
    Assert.That(new IrConvert(IrType.U8, _one), Is.EqualTo(new IrConvert(IrType.U8, _one, IrConversion.Wrap)).And.Not.EqualTo(new IrConvert(IrType.U8, _one, IrConversion.Trap)));
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
    Assert.That(Run(IrType.I8, Arguments(a: -3), new IrReturn(new IrConvert(IrType.I8, Ref(_a)))), Is.EqualTo((sbyte)-3).And.TypeOf<sbyte>());
    Assert.That(Run(IrType.I16, Arguments(a: -3), new IrReturn(new IrConvert(IrType.I16, Ref(_a)))), Is.EqualTo((short)-3).And.TypeOf<short>());
    Assert.That(Run(IrType.U16, Arguments(a: -3), new IrReturn(new IrConvert(IrType.U16, Ref(_a)))), Is.EqualTo((ushort)65533).And.TypeOf<ushort>());
    Assert.That(Run(IrType.I64, Arguments(a: -3), new IrReturn(new IrConvert(IrType.I64, Ref(_a)))), Is.EqualTo(-3L).And.TypeOf<long>());
    Assert.That(Run(IrType.U64, Arguments(a: -3), new IrReturn(new IrConvert(IrType.U64, Ref(_a)))), Is.EqualTo(ulong.MaxValue - 2).And.TypeOf<ulong>());
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
    Assert.That(Create(IrType.I8, 1), Is.TypeOf<sbyte[]>());
    Assert.That(Create(IrType.I16, 1), Is.TypeOf<short[]>());
    Assert.That(Create(IrType.U16, 1), Is.TypeOf<ushort[]>());
    Assert.That(Create(IrType.I64, 1), Is.TypeOf<long[]>());
    Assert.That(Create(IrType.U64, 3), Is.EqualTo(new ulong[3]).And.TypeOf<ulong[]>());
    Assert.That(Create(IrType.F32, 1), Is.TypeOf<float[]>());
    Assert.That(Create(IrType.F64, 1), Is.TypeOf<double[]>());
  }

  /// <summary>
  /// Every numeric type crosses the boundary as itself and inside an array; a <c>u16</c> is also a
  /// <see cref="char"/>, and a <c>char[]</c> stays one when it is written.
  /// </summary>
  [Test]
  public void Interpreter_TakesEveryNumericTypeAcrossTheBoundary() {
    object? Pass(IrType type, object value) {
      var p = new IrLocal("p", type);
      return IrInterpreter.Call(new IrModule("M", [Function("Same", type, [p], [], new IrReturn(Ref(p)))]), "Same", [value]);
    }

    object? Swap(IrType element, Array data) {
      var d = new IrLocal("d", IrType.Array(element));
      var first = new IrArrayLoad(Ref(d), new IrConst(IrType.I32, 0));
      var module = new IrModule("M", [Function("Swap", element, [d], [],
        new IrStore(Ref(d), new IrConst(IrType.I32, 1), first), new IrReturn(first))]);
      return IrInterpreter.Call(module, "Swap", [data]);
    }

    Assert.That(Pass(IrType.I8, sbyte.MinValue), Is.EqualTo(sbyte.MinValue).And.TypeOf<sbyte>());
    Assert.That(Pass(IrType.I16, short.MinValue), Is.EqualTo(short.MinValue).And.TypeOf<short>());
    Assert.That(Pass(IrType.U16, ushort.MaxValue), Is.EqualTo(ushort.MaxValue).And.TypeOf<ushort>());
    Assert.That(Pass(IrType.U16, 'A'), Is.EqualTo((ushort)65).And.TypeOf<ushort>());
    Assert.That(Pass(IrType.I64, long.MinValue), Is.EqualTo(long.MinValue).And.TypeOf<long>());
    Assert.That(Pass(IrType.U64, ulong.MaxValue), Is.EqualTo(ulong.MaxValue).And.TypeOf<ulong>());
    Assert.That(Pass(IrType.F32, 0.1f), Is.EqualTo(0.1f).And.TypeOf<float>());
    Assert.That(Pass(IrType.F64, 0.1), Is.EqualTo(0.1).And.TypeOf<double>());
    var singles = new[] { 0.1f, 0f };
    Assert.That((Swap(IrType.F32, singles), singles[1]), Is.EqualTo((0.1f, 0.1f)));
    var doubles = new[] { -0.1, 0.0 };
    Assert.That((Swap(IrType.F64, doubles), doubles[1]), Is.EqualTo((-0.1, -0.1)));
    Assert.That(() => Pass(IrType.F64, 0.1f), Throws.ArgumentException.With.Message.Contains("'p' is not a value of f64"));

    var letters = new[] { 'x', 'y' };
    Assert.That(Swap(IrType.U16, letters), Is.EqualTo((ushort)'x'));
    Assert.That(letters, Is.EqualTo(new[] { 'x', 'x' }));
    var signed = new sbyte[] { -1, 0 };
    Assert.That((Swap(IrType.I8, signed), signed[1]), Is.EqualTo(((sbyte)-1, (sbyte)-1)));
    var shorts = new short[] { -2, 0 };
    Assert.That((Swap(IrType.I16, shorts), shorts[1]), Is.EqualTo(((short)-2, (short)-2)));
    var units = new ushort[] { 65535, 0 };
    Assert.That((Swap(IrType.U16, units), units[1]), Is.EqualTo((ushort.MaxValue, ushort.MaxValue)));
    var longs = new[] { long.MinValue, 0 };
    Assert.That((Swap(IrType.I64, longs), longs[1]), Is.EqualTo((long.MinValue, long.MinValue)));
    var large = new[] { ulong.MaxValue, 0UL };
    Assert.That((Swap(IrType.U64, large), large[1]), Is.EqualTo((ulong.MaxValue, ulong.MaxValue)));

    Assert.That(() => Pass(IrType.I64, 1), Throws.ArgumentException.With.Message.Contains("'p' is not a value of i64"));
    Assert.That(() => Pass(IrType.Array(IrType.I32), new[] { new[] { 1 } }), Throws.ArgumentException);
    Assert.That(() => Pass(IrType.Array(IrType.I32), new int[1, 1]), Throws.ArgumentException);
    Assert.That(() => Pass(IrType.Array(IrType.I32), new[] { true }), Throws.ArgumentException);
    Assert.That(() => Pass(IrType.I32, 1.5m), Throws.ArgumentException);
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
