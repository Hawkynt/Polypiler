using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Records;

/// <summary>
/// The value records of the IR built by hand: the declarations, the verifier's rules for them, and
/// the interpreter, which copies on every read of a struct value.
/// </summary>
[TestFixture]
public sealed class IrRecordTests {

  private static readonly IrStructType _vec = new("Vec", [new IrField("X", IrType.F64, false), new IrField("Y", IrType.F64, false)]);
  private static readonly IrType _vecType = IrType.Struct(_vec);
  private static readonly IrField _x = _vec.Fields[0];
  private static readonly IrField _y = _vec.Fields[1];
  private static readonly IrLocal _p = new("p", _vecType);
  private static readonly IrLocal _q = new("q", _vecType);
  private static readonly IrLocal _a = new("a", IrType.Array(_vecType));
  private static readonly IrLocal _i = new("i", IrType.I32);

  private static IrConst F(double value) => new(IrType.F64, IrOperators.Bits(IrType.F64, value));

  private static IrConst I(int value) => new(IrType.I32, value);

  private static IrExpr Ref(IrLocal local) => new IrLocalRef(local);

  private static IrStructNew Vec(double x, double y) => new(_vecType, [F(x), F(y)]);

  /// <summary>A module of the given types with one public function F.</summary>
  private static IrModule Module(IrType returns, IrLocal[] parameters, IrLocal[] locals, IrStmt[] body, params IrTypeDeclaration[] types)
    => new("M", [new IrFunction("F", true, returns, parameters, locals, new IrBlock(body))]) { Types = types.Length == 0 ? [_vec] : types };

  private static IrModule Statements(params IrStmt[] body) => Module(IrType.Void, [_p, _a, _i], [_q], body);

  private static object? Run(IrType returns, IrLocal[] locals, params IrStmt[] body) {
    var module = Module(returns, [], locals, body);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    return IrInterpreter.Call(module, "F", []);
  }

  private static IEnumerable<TestCaseData> IllTyped() {
    TestCaseData Case(string name, IrModule module, string message) => new TestCaseData(module, message).SetName("IllTyped: " + name);
    var other = new IrStructType("Other", [new IrField("X", IrType.F64, false)]);
    var frozen = new IrStructType("Frozen", [new IrField("X", IrType.F64, true)]);
    var self = new IrStructType("Self");
    self.Define([new IrField("Me", IrType.Struct(self), false)]);
    var first = new IrStructType("First");
    var second = new IrStructType("Second", [new IrField("Back", IrType.Struct(first), false)]);
    first.Define([new IrField("On", IrType.Struct(second), false)]);
    var buffer = new IrStructType("Buffer", [new IrField("Data", IrType.Array(IrType.U8), false)]);
    var holder = new IrStructType("Holder", [new IrField("Buffer", IrType.Struct(buffer), false)]);
    var undefined = new IrStructType("Undefined");

    yield return Case("field of another type", Statements(new IrExprStmt(new IrFieldLoad(Ref(_p), other.Fields[0]))), "the field 'X' is not a field of Vec");
    yield return Case("missing field", Statements(new IrDeclare(_q, new IrStructNew(_vecType, [F(1)]))), "the struct Vec is built from 1 values for 2 fields");
    yield return Case("field given twice", Statements(new IrDeclare(_q, new IrStructNew(_vecType, [F(1), F(2), F(3)]))), "the struct Vec is built from 3 values for 2 fields");
    yield return Case("field of the wrong type", Statements(new IrDeclare(_q, new IrStructNew(_vecType, [F(1), I(2)]))), "the field 'Y' of Vec is given the type i32, expected f64");
    yield return Case("store to a read-only field", Module(IrType.Void, [new IrLocal("f", IrType.Struct(frozen))], [], [new IrFieldStore(Ref(new IrLocal("f", IrType.Struct(frozen))), frozen.Fields[0], F(1))], frozen),
      "the read-only field 'X' is written");
    yield return Case("self-containing struct", Module(IrType.Void, [], [], [], self), "ill-typed IR in 'Self': the struct contains itself");
    yield return Case("structs that contain each other", Module(IrType.Void, [], [], [], first, second), "ill-typed IR in 'First': the struct contains itself");
    yield return Case("type declared twice", Module(IrType.Void, [], [], [], _vec, _vec), "ill-typed IR in 'Vec': the module declares the type twice");
    yield return Case("field declared twice", Module(IrType.Void, [], [], [], new IrStructType("Twice", [new IrField("A", IrType.I32, false), new IrField("A", IrType.I32, false)])),
      "the field 'A' is declared twice");
    yield return Case("field of void", Module(IrType.Void, [], [], [], new IrStructType("Hollow", [new IrField("A", IrType.Void, false)])), "the field 'A' has the type void");
    yield return Case("field of an array of structs", Module(IrType.Void, [], [], [], new IrStructType("Nested", [new IrField("A", IrType.Array(_vecType), false)]), _vec),
      "the field 'A' has the type Vec[]");
    yield return Case("field of an undeclared struct", Module(IrType.Void, [], [], [], new IrStructType("Outer", [new IrField("A", _vecType, false)])),
      "the field 'A' has the type Vec, which is not among the types of the module");
    yield return Case("type without fields", Module(IrType.Void, [], [], [], undefined), "ill-typed IR in 'Undefined': a type without its fields");
    yield return Case("type of another kind", Module(IrType.Void, [], [], [], new Opaque()), "ill-typed IR in 'Opaque': a declaration of the kind Opaque");
    yield return Case("parameter of an undeclared struct", Module(IrType.Void, [new IrLocal("o", IrType.Struct(other))], [], []), "the type Other is not among the types of the module");
    yield return Case("local of an array of an undeclared struct", Module(IrType.Void, [], [new IrLocal("o", IrType.Array(IrType.Struct(other)))], []), "the type Other is not among the types of the module");
    yield return Case("result of an undeclared struct", Module(IrType.Struct(other), [], [], [new IrReturn(new IrStructDefault(IrType.Struct(other)))]), "the type Other is not among the types of the module");
    yield return Case("default of a struct with an array", Module(IrType.Void, [], [], [new IrExprStmt(new IrStructDefault(IrType.Struct(holder)))], buffer, holder),
      "the struct Holder has a field of an array type and so no default value");
    yield return Case("array of a struct with an array", Module(IrType.Void, [], [], [new IrExprStmt(new IrNewArray(IrType.Array(IrType.Struct(buffer)), I(1)))], buffer),
      "the struct Buffer has a field of an array type and so no default value");
    yield return Case("new of a number", Statements(new IrExprStmt(new IrStructNew(IrType.I32, []))), "a struct is expected, not the type i32");
    yield return Case("field of a number", Statements(new IrExprStmt(new IrFieldLoad(Ref(_i), _x))), "a struct is expected, not the type i32");
    yield return Case("field store into a value", Statements(new IrFieldStore(Vec(1, 2), _x, F(3))), "a field is written of a IrStructNew, which is no place");
    yield return Case("field store into a call", Statements(new IrFieldStore(new IrCall("F", IrType.Void, []), _x, F(3))), "a field is written of a IrCall, which is no place");
    yield return Case("field store of the wrong type", Statements(new IrFieldStore(Ref(_p), _x, I(3))), "the value stored in the field 'X' has the type i32, expected f64");
    yield return Case("equality of two types", Module(IrType.Void, [_p, new IrLocal("o", IrType.Struct(other))], [], [new IrExprStmt(new IrStructEquals(Ref(_p), Ref(new IrLocal("o", IrType.Struct(other)))))], _vec, other),
      "a struct of the type Vec is compared with one of the type Other");
    yield return Case("equality of numbers", Statements(new IrExprStmt(new IrStructEquals(I(1), I(1)))), "a struct is expected, not the type i32");
    yield return Case("constant of a struct", new IrModule("M", [], [new IrModuleConst("C", _vecType, [0])]) { Types = [_vec] }, "a constant of the type Vec");
    yield return Case("array copy of structs", Statements(new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayCopy, IrType.Void, [Ref(_a), I(0), Ref(_a), I(1), I(1)]))),
      "ArrayCopy with the type void is applied to (Vec[], i32, Vec[], i32, i32)");
    yield return Case("array fill of structs", Statements(new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayFill, IrType.Void, [Ref(_a), Ref(_p)]))), "ArrayFill with the type void");
    yield return Case("array clear of structs", Statements(new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayClear, IrType.Void, [Ref(_a)]))), "ArrayClear with the type void");
  }

  private sealed class Opaque() : IrTypeDeclaration("Opaque") {

    public Opaque Defined() {
      this.Define([]);
      return this;
    }

  }

  /// <summary>Test design 12: the verifier refuses what no value record can be.</summary>
  [TestCaseSource(nameof(IllTyped))]
  public void Verifier_RefusesIllTypedRecords(IrModule module, string message) {
    if (module.Types.OfType<Opaque>().FirstOrDefault() is { IsDefined: false } opaque)
      opaque.Defined();

    var problems = IrVerifier.Verify(module);
    Assert.That(problems.Select(problem => problem.Message), Has.Some.Contains(message));
    Assert.That(problems.Select(problem => problem.Code), Is.All.EqualTo(IrVerifier.Code));
  }

  /// <summary>A field store is a step of a for: what follows every pass may write a field.</summary>
  [Test]
  public void Verifier_AcceptsAFieldStoreAsTheStepOfAFor() {
    var loop = new IrFor(new IrConst(IrType.Bool, 0), new IrBlock([]), new IrBlock([new IrFieldStore(Ref(_p), _x, F(1))]));
    Assert.That(IrVerifier.Verify(Statements(loop)), Is.Empty);
  }

  /// <summary>A field of an array element and a field of a field are places; so is a field of a field of an element.</summary>
  [Test]
  public void Verifier_AcceptsEveryPlace() {
    var outer = new IrStructType("Outer", [new IrField("V", _vecType, false)]);
    var o = new IrLocal("o", IrType.Struct(outer));
    var elements = new IrLocal("e", IrType.Array(IrType.Struct(outer)));
    var module = Module(IrType.Void, [o, elements], [], [
      new IrFieldStore(new IrFieldLoad(Ref(o), outer.Fields[0]), _x, F(1)),
      new IrFieldStore(new IrFieldLoad(new IrArrayLoad(Ref(elements), I(0)), outer.Fields[0]), _y, F(2))
    ], _vec, outer);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }

  /// <summary>A declaration is given its fields once, and a field is its declaration's by identity.</summary>
  [Test]
  public void Declaration_IsDefinedOnce_AndOwnsItsFieldsByIdentity() {
    var late = new IrStructType("Late");
    Assert.That(late.IsDefined, Is.False);
    Assert.That(() => late.Fields, Throws.InvalidOperationException.With.Message.Contains("'Late' are not defined yet"));
    late.Define([new IrField("X", IrType.F64, false)]);
    Assert.That(() => late.Define([]), Throws.InvalidOperationException.With.Message.Contains("'Late' are defined already"));
    Assert.That(late.Owns(late.Fields[0]), Is.True);
    Assert.That(late.Owns(new IrField("X", IrType.F64, false)), Is.False);
    Assert.That(late.Field("X"), Is.SameAs(late.Fields[0]));
    Assert.That(late.Field("Z"), Is.Null);
    Assert.That(late.ToString(), Is.EqualTo("Late"));
  }

  /// <summary>Structs are nominal, have a default exactly when no field is an array, and are read-only when every field is.</summary>
  [Test]
  public void StructTypes_AreNominal() {
    var twin = new IrStructType("Vec", [new IrField("X", IrType.F64, false), new IrField("Y", IrType.F64, false)]);
    Assert.That(IrType.Struct(twin), Is.Not.EqualTo(_vecType));
    Assert.That(IrType.Struct(_vec), Is.EqualTo(_vecType));
    Assert.That(_vecType.ToString(), Is.EqualTo("Vec"));
    Assert.That(IrType.Array(_vecType).ToString(), Is.EqualTo("Vec[]"));
    Assert.That((_vecType.IsStruct, _vecType.StructType, _vecType.IsStorable, IrType.Array(_vecType).IsStorable), Is.EqualTo((true, _vec, true, true)));
    Assert.That(IrType.I32.StructType, Is.Null);
    Assert.That(new IrStructType("Frozen", [new IrField("X", IrType.I32, true)]).IsReadOnly, Is.True);
    Assert.That(_vec.IsReadOnly, Is.False);
    var buffer = new IrStructType("Buffer", [new IrField("Data", IrType.Array(IrType.U8), true)]);
    Assert.That(new IrStructType("Outer", [new IrField("B", IrType.Struct(buffer), false)]).HasDefault, Is.False);
    Assert.That(new IrStructType("Outer", [new IrField("V", _vecType, false)]).HasDefault, Is.True);
  }

  /// <summary>Test design 2: an assignment, an argument, a result, an array store and load and a nested field each copy.</summary>
  [Test]
  public void Interpreter_CopiesOnEveryRead() {
    // q = p; q.X = 5; result p.X
    Assert.That(Run(IrType.F64, [_p, _q], new IrDeclare(_p, Vec(1, 2)), new IrDeclare(_q, Ref(_p)), new IrFieldStore(Ref(_q), _x, F(5)), new IrReturn(new IrFieldLoad(Ref(_p), _x))), Is.EqualTo(1.0));
    // a[0] = p; p.X = 5; result a[0].X
    Assert.That(Run(IrType.F64, [_p, _a],
      new IrDeclare(_p, Vec(1, 2)), new IrDeclare(_a, new IrNewArray(IrType.Array(_vecType), I(1))), new IrStore(Ref(_a), I(0), Ref(_p)),
      new IrFieldStore(Ref(_p), _x, F(5)), new IrReturn(new IrFieldLoad(new IrArrayLoad(Ref(_a), I(0)), _x))), Is.EqualTo(1.0));
    // p = a[0]; p.X = 5; a[0].X = 7; result p.X * 10 + a[0].X
    Assert.That(Run(IrType.F64, [_p, _a],
      new IrDeclare(_a, new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(_vecType), [Vec(1, 2)])), new IrDeclare(_p, new IrArrayLoad(Ref(_a), I(0))),
      new IrFieldStore(Ref(_p), _x, F(5)), new IrFieldStore(new IrArrayLoad(Ref(_a), I(0)), _x, F(7)),
      new IrReturn(new IrBinary(IrBinaryOp.Add, new IrBinary(IrBinaryOp.Multiply, new IrFieldLoad(Ref(_p), _x), F(10)), new IrFieldLoad(new IrArrayLoad(Ref(_a), I(0)), _x)))), Is.EqualTo(57.0));
  }

  /// <summary>An argument and a result are copies: the callee writes its own, the caller's value stays.</summary>
  [Test]
  public void Interpreter_PassesAndReturnsCopies() {
    var v = new IrLocal("v", _vecType);
    var bump = new IrFunction("Bump", false, _vecType, [v], [], new IrBlock([new IrFieldStore(Ref(v), _x, F(9)), new IrReturn(Ref(v))]));
    var r = new IrLocal("r", _vecType);
    var main = new IrFunction("F", true, IrType.F64, [], [_p, r], new IrBlock([
      new IrDeclare(_p, Vec(1, 2)), new IrDeclare(r, new IrCall("Bump", _vecType, [Ref(_p)])), new IrFieldStore(Ref(r), _y, F(4)),
      new IrReturn(new IrBinary(IrBinaryOp.Add, new IrBinary(IrBinaryOp.Multiply, new IrFieldLoad(Ref(_p), _x), F(1000)),
        new IrBinary(IrBinaryOp.Add, new IrBinary(IrBinaryOp.Multiply, new IrFieldLoad(Ref(_p), _y), F(100)),
          new IrBinary(IrBinaryOp.Add, new IrBinary(IrBinaryOp.Multiply, new IrFieldLoad(Ref(r), _x), F(10)), new IrFieldLoad(Ref(r), _y)))))
    ]));
    var module = new IrModule("M", [main, bump]) { Types = [_vec] };
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That(IrInterpreter.Call(module, "F", []), Is.EqualTo(1294.0));
  }

  /// <summary>Test design 7: a nesting of depth 3 copies all the way down, and a field of a field is written in place.</summary>
  [Test]
  public void Interpreter_CopiesNestedStructsDeeply() {
    var inner = new IrStructType("Inner", [new IrField("V", _vecType, false)]);
    var outer = new IrStructType("Outer", [new IrField("I", IrType.Struct(inner), false)]);
    var o = new IrLocal("o", IrType.Struct(outer));
    var c = new IrLocal("c", IrType.Struct(outer));
    IrExpr Deep(IrLocal local) => new IrFieldLoad(new IrFieldLoad(new IrFieldLoad(Ref(local), outer.Fields[0]), inner.Fields[0]), _x);
    var module = Module(IrType.F64, [], [o, c], [
      new IrDeclare(o, new IrStructDefault(IrType.Struct(outer))),
      new IrDeclare(c, Ref(o)),
      new IrFieldStore(new IrFieldLoad(new IrFieldLoad(Ref(c), outer.Fields[0]), inner.Fields[0]), _x, F(3)),
      new IrReturn(new IrBinary(IrBinaryOp.Add, new IrBinary(IrBinaryOp.Multiply, Deep(o), F(10)), Deep(c)))
    ], _vec, inner, outer);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That(IrInterpreter.Call(module, "F", []), Is.EqualTo(3.0));
  }

  /// <summary>Test design 6: float fields compare as double.Equals does.</summary>
  [TestCase(double.NaN, double.NaN, true)]
  [TestCase(0.0, -0.0, true)]
  [TestCase(1.0, 1.0000000000000002, false)]
  [TestCase(2.5, 2.5, true)]
  public void Interpreter_ComparesFloatFieldsAsDoubleEquals(double left, double right, bool equal) {
    Assert.That(Run(IrType.Bool, [], new IrReturn(new IrStructEquals(Vec(left, 1), Vec(right, 1)))), Is.EqualTo(equal));
    var single = new IrStructType("S", [new IrField("F", IrType.F32, false)]);
    IrExpr S(double value) => new IrStructNew(IrType.Struct(single), [new IrConst(IrType.F32, IrOperators.Bits(IrType.F32, value))]);
    var module = Module(IrType.Bool, [], [], [new IrReturn(new IrStructEquals(S(left), S(right)))], single);
    Assert.That(IrInterpreter.Call(module, "F", []), Is.EqualTo(equal || (float)left == (float)right));
  }

  /// <summary>Test design 3: every field type has its zero as default; integers, truth values, nested structs and arrays compare by value or identity.</summary>
  [Test]
  public void Interpreter_ComparesEveryFieldType() {
    var kinds = new[] { IrType.Bool, IrType.I8, IrType.U8, IrType.I16, IrType.U16, IrType.I32, IrType.U32, IrType.I64, IrType.U64, IrType.F32, IrType.F64 };
    var all = new IrStructType("All", [.. kinds.Select((kind, index) => new IrField("F" + index, kind, false)), new IrField("V", _vecType, false)]);
    var type = IrType.Struct(all);
    var module = Module(IrType.Bool, [], [], [new IrReturn(new IrStructEquals(new IrStructDefault(type),
      new IrStructNew(type, [.. kinds.Select(kind => new IrConst(kind, 0)), Vec(0, 0)])))], _vec, all);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That(IrInterpreter.Call(module, "F", []), Is.True);
    var different = Module(IrType.Bool, [], [], [new IrReturn(new IrStructEquals(new IrStructDefault(type),
      new IrStructNew(type, [.. kinds.Select(kind => new IrConst(kind, 0)), Vec(0, 1)])))], _vec, all);
    Assert.That(IrInterpreter.Call(different, "F", []), Is.False);

    var holder = new IrStructType("Holder", [new IrField("Data", IrType.Array(IrType.I32), false)]);
    var data = new IrLocal("data", IrType.Array(IrType.I32));
    IrExpr Hold(IrExpr array) => new IrStructNew(IrType.Struct(holder), [array]);
    var arrays = Module(IrType.Bool, [data], [], [new IrReturn(new IrBinary(IrBinaryOp.LogicalAnd,
      new IrStructEquals(Hold(Ref(data)), Hold(Ref(data))),
      new IrUnary(IrUnaryOp.LogicalNot, new IrStructEquals(Hold(Ref(data)), Hold(new IrNewArray(IrType.Array(IrType.I32), I(1)))))))], holder);
    Assert.That(IrVerifier.Verify(arrays), Is.Empty);
    Assert.That(IrInterpreter.Call(arrays, "F", [new[] { 1 }]), Is.True);
  }

  /// <summary>Test design 7 and 8: an empty struct, a struct of 16 fields, arrays of 0, 1 and 2 elements and the indices around them.</summary>
  [Test]
  public void Interpreter_HandlesTheBoundaries() {
    var empty = new IrStructType("Empty", []);
    Assert.That(IrInterpreter.Call(Module(IrType.Bool, [], [], [new IrReturn(new IrStructEquals(new IrStructNew(IrType.Struct(empty), []), new IrStructDefault(IrType.Struct(empty))))], empty), "F", []), Is.True);
    var wide = new IrStructType("Wide", [.. Enumerable.Range(0, 16).Select(index => new IrField("F" + index, IrType.I32, false))]);
    var w = new IrLocal("w", IrType.Struct(wide));
    var sixteen = Module(IrType.I32, [], [w], [
      new IrDeclare(w, new IrStructNew(IrType.Struct(wide), [.. Enumerable.Range(0, 16).Select(I)])),
      new IrReturn(new IrFieldLoad(Ref(w), wide.Fields[15]))
    ], wide);
    Assert.That(IrInterpreter.Call(sixteen, "F", []), Is.EqualTo(15));

    var n = new IrLocal("n", IrType.I32);
    var index = new IrLocal("index", IrType.I32);
    var elements = Module(IrType.F64, [n, index], [_a], [
      new IrDeclare(_a, new IrNewArray(IrType.Array(_vecType), Ref(n))),
      new IrReturn(new IrFieldLoad(new IrArrayLoad(Ref(_a), Ref(index)), _y))
    ]);
    foreach (var length in new[] { 1, 2 })
      Assert.That(IrInterpreter.Call(elements, "F", [length, length - 1]), Is.EqualTo(0.0));

    foreach (var (length, at) in new[] { (0, 0), (2, -1), (2, 2) })
      Assert.That(() => IrInterpreter.Call(elements, "F", [length, at]), Throws.TypeOf<IrTrapException>().With.Property("Trap").EqualTo(IrTrap.IndexOutOfRange));

    Assert.That(() => IrInterpreter.Call(elements, "F", [-1, 0]), Throws.TypeOf<IrTrapException>().With.Property("Trap").EqualTo(IrTrap.IntegerOverflow));
    var store = Module(IrType.Void, [n, index], [_a], [
      new IrDeclare(_a, new IrNewArray(IrType.Array(_vecType), Ref(n))),
      new IrFieldStore(new IrArrayLoad(Ref(_a), Ref(index)), _x, F(1))
    ]);
    Assert.That(() => IrInterpreter.Call(store, "F", [1, 1]), Throws.TypeOf<IrTrapException>().With.Property("Trap").EqualTo(IrTrap.IndexOutOfRange));
  }

  /// <summary>A struct crosses the boundary of the interpreter as an array of its field values, a nested one likewise.</summary>
  [Test]
  public void Interpreter_TakesAndReturnsStructsAsArraysOfFields() {
    var outer = new IrStructType("Outer", [new IrField("V", _vecType, false), new IrField("N", IrType.U16, false)]);
    var o = new IrLocal("o", IrType.Struct(outer));
    var module = Module(IrType.Struct(outer), [o], [], [new IrFieldStore(new IrFieldLoad(Ref(o), outer.Fields[0]), _y, F(9)), new IrReturn(Ref(o))], _vec, outer);
    Assert.That(IrInterpreter.Call(module, "F", [new object[] { new object[] { 1.0, 2.0 }, (ushort)7 }]), Is.EqualTo(new object[] { new object[] { 1.0, 9.0 }, (ushort)7 }));
    Assert.That(IrInterpreter.Call(module, "F", [new object[] { new object[] { 1.0, 2.0 }, 'c' }]), Is.EqualTo(new object[] { new object[] { 1.0, 9.0 }, (ushort)'c' }));
    foreach (var wrong in new object[] { 1.0, new object[] { new object[] { 1.0 }, (ushort)7 }, new object[] { new object[] { 1.0, 2.0 } }, new object?[] { new object[] { 1.0, 2.0 }, null }, new object[] { new object[] { 1, 2.0 }, (ushort)7 } })
      Assert.That(() => IrInterpreter.Call(module, "F", [wrong]), Throws.ArgumentException.With.Message.Contains("is not a value of Outer"));
  }

  /// <summary>Every node of a function, the record nodes included, is reached by the tree walk, in evaluation order.</summary>
  [Test]
  public void Tree_ReachesEveryNode() {
    var body = new IrBlock([
      new IrFieldStore(Ref(_p), _x, new IrFieldLoad(Vec(1, 2), _y)),
      new IrIf(new IrStructEquals(Ref(_p), new IrStructDefault(_vecType)), new IrBlock([new IrReturn(null)]), new IrBlock([new IrExprStmt(I(1))])),
      new IrDoWhile(new IrBlock([]), new IrConst(IrType.Bool, 0)),
      new IrSwitch(I(2), [new IrSwitchSection([1], false, new IrBlock([new IrStore(Ref(_a), I(3), Ref(_q))]))])
    ]);
    var kinds = IrTree.Nodes(body).Select(node => node.GetType().Name).ToList();
    Assert.That(kinds, Is.EqualTo(new[] {
      "IrLocalRef", "IrFieldLoad", "IrStructNew", "IrConst", "IrConst", "IrStructEquals", "IrLocalRef", "IrStructDefault",
      "IrConst", "IrConst", "IrConst", "IrLocalRef", "IrConst", "IrLocalRef"
    }));
    Assert.That(IrTree.Blocks(new IrBreak()), Is.Empty);
    Assert.That(IrTree.Expressions(new IrBreak()), Is.Empty);
  }

}
