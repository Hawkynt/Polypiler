using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Optional;

/// <summary>
/// Optional values in the IR on modules built by hand: the type, the rules of the verifier and what
/// the interpreter does with none, a value, the trap of an unwrapped none and the boundary.
/// </summary>
[TestFixture]
public sealed class IrOptionalTests {

  private static readonly IrType Int = IrType.Optional(IrType.I32);

  private static IrFunction F(IrType result, IReadOnlyList<IrLocal> parameters, IReadOnlyList<IrLocal> locals, params IrStmt[] statements)
    => new("F", true, result, parameters, locals, new IrBlock(statements));

  private static IrModule Module(params IrFunction[] functions) => new("M", functions);

  private static IReadOnlyList<string> Problems(IrModule module) => [.. IrVerifier.Verify(module).Select(diagnostic => diagnostic.Message)];

  private static IrConst I(int value) => new(IrType.I32, value);

  [Test]
  public void Type_IsOptionalOverScalarsAndStructsOnly() {
    Assert.That(Int.ToString(), Is.EqualTo("i32?"));
    Assert.That(Int, Is.EqualTo(IrType.Optional(IrType.I32)));
    Assert.That(Int, Is.Not.EqualTo(IrType.Optional(IrType.U32)));
    Assert.That(Int.IsOptional && Int.IsStorable && Int.Underlying == IrType.I32, Is.True);
    Assert.That(IrType.Optional(IrType.Bool).IsStorable, Is.True);
    Assert.That(IrType.Optional(IrType.F64).IsStorable, Is.True);
    Assert.That(IrType.Optional(IrType.String).IsStorable, Is.False);
    Assert.That(IrType.Optional(IrType.Array(IrType.I32)).IsStorable, Is.False);
    Assert.That(IrType.Optional(Int).IsStorable, Is.False);
    Assert.That(IrType.I32.Underlying, Is.Null);
    Assert.That(IrType.Array(IrType.I32).IsNullable && !IrType.I32.IsNullable && !IrType.String.IsNullable, Is.True);
  }

  /// <summary>Test design 2: optional values over every scalar type hold their value and none, and compare as the lifting written out says.</summary>
  [TestCase(IrTypeKind.I8, 5L), TestCase(IrTypeKind.U8, 200L), TestCase(IrTypeKind.I16, -3L), TestCase(IrTypeKind.U16, 65L), TestCase(IrTypeKind.I32, -7L),
   TestCase(IrTypeKind.U32, 9L), TestCase(IrTypeKind.I64, long.MinValue), TestCase(IrTypeKind.U64, -1L), TestCase(IrTypeKind.Bool, 1L)]
  public void EveryScalar_HoldsAValueOrNone(IrTypeKind kind, long bits) {
    var type = new[] { IrType.I8, IrType.U8, IrType.I16, IrType.U16, IrType.I32, IrType.U32, IrType.I64, IrType.U64, IrType.Bool }.Single(candidate => candidate.Kind == kind);
    var optional = IrType.Optional(type);
    var x = new IrLocal("x", optional);
    var fallback = new IrLocal("d", type);
    var module = Module(F(type, [x, fallback], [], new IrReturn(new IrCoalesce(new IrLocalRef(x), new IrLocalRef(fallback)))));
    Assert.That(Problems(module), Is.Empty);
    var someValue = IrInterpreter.Call(module, "F", [Host(type, bits), Host(type, 0)]);
    Assert.That(someValue, Is.EqualTo(Host(type, bits)));
    Assert.That(IrInterpreter.Call(module, "F", [null!, Host(type, 0)]), Is.EqualTo(Host(type, 0)));
  }

  private static object Host(IrType type, long bits) => type.Kind switch {
    IrTypeKind.I8 => (sbyte)bits,
    IrTypeKind.U8 => (byte)bits,
    IrTypeKind.I16 => (short)bits,
    IrTypeKind.U16 => (ushort)bits,
    IrTypeKind.I32 => (int)bits,
    IrTypeKind.U32 => (uint)bits,
    IrTypeKind.I64 => bits,
    IrTypeKind.U64 => (ulong)bits,
    _ => bits != 0
  };

  /// <summary>Test design 5: the left side of ?? is the result whenever it holds a value, also 0, false, NaN and -0.0.</summary>
  [TestCase(0.0), TestCase(double.NaN), TestCase(-0.0), TestCase(2.5)]
  public void Coalesce_KeepsAPresentValue(double value) {
    var x = new IrLocal("x", IrType.Optional(IrType.F64));
    var module = Module(F(IrType.F64, [x], [], new IrReturn(new IrCoalesce(new IrLocalRef(x), new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, 1))))));
    var result = (double)IrInterpreter.Call(module, "F", [value])!;
    Assert.That(System.BitConverter.DoubleToInt64Bits(result), Is.EqualTo(System.BitConverter.DoubleToInt64Bits(value)));
    Assert.That(IrInterpreter.Call(module, "F", [null!]), Is.EqualTo(1.0));
  }

  [Test]
  public void Coalesce_OfFalseIsFalse() {
    var x = new IrLocal("x", IrType.Optional(IrType.Bool));
    var module = Module(F(IrType.Bool, [x], [], new IrReturn(new IrCoalesce(new IrLocalRef(x), new IrConst(IrType.Bool, 1)))));
    Assert.That(IrInterpreter.Call(module, "F", [false]), Is.EqualTo(false));
    Assert.That(IrInterpreter.Call(module, "F", [null!]), Is.EqualTo(true));
  }

  /// <summary>Test design 8: the optional value of ?? is evaluated once, the fallback only when it holds none.</summary>
  [Test]
  public void Coalesce_EvaluatesTheValueOnceAndTheFallbackOnlyWhenNeeded() {
    var counter = new IrGlobal("calls", IrType.I32, I(0));
    var mode = new IrLocal("present", IrType.Bool);
    var next = new IrFunction("Next", false, Int, [mode], [], new IrBlock([
      new IrGlobalAssign(counter, new IrBinary(IrBinaryOp.Add, new IrGlobalRef(counter), I(1))),
      new IrReturn(new IrConditional(new IrLocalRef(mode), new IrSome(I(4)), new IrNone(Int)))
    ]));
    var other = new IrFunction("Other", false, IrType.I32, [], [], new IrBlock([
      new IrGlobalAssign(counter, new IrBinary(IrBinaryOp.Add, new IrGlobalRef(counter), I(100))),
      new IrReturn(I(-1))
    ]));
    var present = new IrLocal("p", IrType.Bool);
    var main = F(IrType.I32, [present], [], new IrExprStmt(new IrCoalesce(new IrCall("Next", Int, [new IrLocalRef(present)]), new IrCall("Other", IrType.I32, []))), new IrReturn(new IrGlobalRef(counter)));
    var module = new IrModule("M", [next, other, main]) { Globals = [counter] };
    Assert.That(Problems(module), Is.Empty);
    Assert.That(IrInterpreter.Call(module, "F", [true]), Is.EqualTo(1));
    Assert.That(IrInterpreter.Call(module, "F", [false]), Is.EqualTo(101));
  }

  /// <summary>Test design 9: the value of none traps with MissingValue.</summary>
  [Test]
  public void Unwrap_OfNoneTraps() {
    var x = new IrLocal("x", Int);
    var module = Module(F(IrType.I32, [x], [], new IrReturn(new IrUnwrap(new IrLocalRef(x)))));
    Assert.That(IrInterpreter.Call(module, "F", [3]), Is.EqualTo(3));
    var trap = Assert.Throws<IrTrapException>(() => IrInterpreter.Call(module, "F", [null!]));
    Assert.That(trap!.Trap, Is.EqualTo(IrTrap.MissingValue));
    Assert.That(trap.Message, Is.EqualTo("missing value"));
    Assert.That(new IrUnwrap(new IrLocalRef(x)) { IsTested = true }.IsTested, Is.True);
  }

  /// <summary>Test design 3: lifted arithmetic and comparisons written out give C#'s results.</summary>
  [Test]
  public void LiftedOperations_WrittenOut_GiveTheLiftedResults() {
    var a = new IrLocal("a", Int);
    var b = new IrLocal("b", Int);
    IrExpr A() => new IrLocalRef(a);
    IrExpr B() => new IrLocalRef(b);
    IrExpr Either() => new IrBinary(IrBinaryOp.LogicalOr, new IrIsNone(A()), new IrIsNone(B()));
    IrExpr Value(IrExpr x) => new IrUnwrap(x) { IsTested = true };
    var add = new IrFunction("Add", true, Int, [a, b], [], new IrBlock([new IrReturn(new IrConditional(Either(), new IrNone(Int), new IrSome(new IrBinary(IrBinaryOp.Add, Value(A()), Value(B())))))]));
    var less = new IrFunction("Less", true, IrType.Bool, [a, b], [], new IrBlock([new IrReturn(new IrBinary(IrBinaryOp.LogicalAnd, new IrUnary(IrUnaryOp.LogicalNot, Either()), new IrBinary(IrBinaryOp.Less, Value(A()), Value(B()))))]));
    var equal = new IrFunction("Equal", true, IrType.Bool, [a, b], [], new IrBlock([new IrReturn(new IrConditional(new IrIsNone(A()), new IrIsNone(B()),
      new IrBinary(IrBinaryOp.LogicalAnd, new IrUnary(IrUnaryOp.LogicalNot, new IrIsNone(B())), new IrBinary(IrBinaryOp.Equal, Value(A()), Value(B())))))]));
    var module = new IrModule("M", [add, less, equal]);
    Assert.That(Problems(module), Is.Empty);
    Assert.That(IrInterpreter.Call(module, "Add", [null!, 1]), Is.Null);
    Assert.That(IrInterpreter.Call(module, "Add", [2, 1]), Is.EqualTo(3));
    Assert.That(IrInterpreter.Call(module, "Less", [null!, 1]), Is.EqualTo(false));
    Assert.That(IrInterpreter.Call(module, "Less", [0, 1]), Is.EqualTo(true));
    Assert.That(IrInterpreter.Call(module, "Equal", [null!, null!]), Is.EqualTo(true));
    Assert.That(IrInterpreter.Call(module, "Equal", [null!, 1]), Is.EqualTo(false));
    Assert.That(IrInterpreter.Call(module, "Equal", [1, null!]), Is.EqualTo(false));
    Assert.That(IrInterpreter.Call(module, "Equal", [1, 1]), Is.EqualTo(true));
  }

  /// <summary>The reference of ?. is evaluated once into a local by IrBind; null arrays cross the boundary as null.</summary>
  [Test]
  public void NullConditional_OnAnArray_EvaluatesTheReceiverOnce() {
    var array = IrType.Array(IrType.I32);
    var a = new IrLocal("a", array);
    var t = new IrLocal("t", array);
    var length = new IrConditional(new IrIsNull(new IrBind(t, new IrLocalRef(a))), new IrNone(Int), new IrSome(new IrArrayLength(new IrLocalRef(t))));
    var module = Module(F(IrType.I32, [a], [t], new IrDeclare(t, null), new IrReturn(new IrCoalesce(length, I(-1)))));
    Assert.That(Problems(module), Is.Empty);
    Assert.That(IrInterpreter.Call(module, "F", [null!]), Is.EqualTo(-1));
    Assert.That(IrInterpreter.Call(module, "F", [new int[3]]), Is.EqualTo(3));
  }

  [Test]
  public void NullArray_IsAValueThatTrapsWhenReached() {
    var array = IrType.Array(IrType.I32);
    var module = Module(F(IrType.I32, [], [], new IrReturn(new IrArrayLength(new IrNull(array)))));
    Assert.That(Problems(module), Is.Empty);
    Assert.That(Assert.Throws<IrTrapException>(() => IrInterpreter.Call(module, "F", []))!.Trap, Is.EqualTo(IrTrap.NullReference));
  }

  /// <summary>An optional value is no field of a struct or a class yet: those are of 17-lists-maps-sets and later.</summary>
  [Test]
  public void OptionalField_IsRefused() {
    var type = new IrStructType("S");
    type.Define([new IrField("v", Int, false)]);
    var module = new IrModule("M", []) { Types = [type] };
    Assert.That(Problems(module), Has.Some.Contains("the field 'v' has the type i32?"));
  }

  /// <summary>The function records the spelling of its absent result for JavaScript; null unless said otherwise.</summary>
  [Test]
  public void Function_RecordsTheSpellingOfItsAbsentResult() {
    var function = F(Int, [], [], new IrReturn(new IrNone(Int)));
    Assert.That(function.Absent, Is.EqualTo(IrAbsent.Null));
    Assert.That((function with { Absent = IrAbsent.Undefined }).Absent, Is.EqualTo(IrAbsent.Undefined));
    Assert.That(IrInterpreter.Call(Module(function), "F", []), Is.Null);
  }

  /// <summary>Test design 11: what the verifier refuses of optional values, one rule each.</summary>
  [Test]
  public void Verifier_RefusesEveryBrokenRule() {
    var x = new IrLocal("x", Int);
    var n = new IrLocal("n", IrType.I32);
    var s = new IrLocal("s", IrType.String);
    void Refused(IrStmt statement, string message, IrLocal? local = null) {
      var module = Module(F(IrType.Void, [x, n, s], local is null ? [] : [local], statement));
      Assert.That(Problems(module), Has.Some.Contains(message), string.Join("\n", Problems(module)));
    }

    Refused(new IrExprStmt(new IrUnwrap(new IrLocalRef(n))), "is unwrapped");
    Refused(new IrExprStmt(new IrIsNone(new IrLocalRef(n))), "is tested for none");
    Refused(new IrExprStmt(new IrSome(new IrLocalRef(s))), "is made optional");
    Refused(new IrExprStmt(new IrNone(IrType.I32)), "which is not optional");
    Refused(new IrExprStmt(new IrCoalesce(new IrLocalRef(x), new IrConst(IrType.I64, 1))), "with a fallback of the type i64");
    Refused(new IrExprStmt(new IrCoalesce(new IrLocalRef(n), new IrConst(IrType.I32, 1))), "is coalesced");
    Refused(new IrExprStmt(new IrIsNull(new IrLocalRef(n))), "is tested for null");
    Refused(new IrExprStmt(new IrBind(n, new IrConst(IrType.I64, 1))), "expected i32");
    var outside = new IrLocal("o", IrType.I32);
    Refused(new IrExprStmt(new IrBind(outside, I(1))), "outside the scope", outside);
    Refused(new IrExprStmt(new IrConst(Int, 0)), "is not a value of i32?");
    var table = new IrModule("M", [], [new IrModuleConst("k", Int, [0])]);
    Assert.That(Problems(table), Has.Some.Contains("a constant of the type i32?"));
  }

}
