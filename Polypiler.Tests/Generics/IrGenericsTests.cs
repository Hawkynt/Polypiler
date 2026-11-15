using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Generics;

/// <summary>
/// The generics of the IR on modules built by hand: the names of instantiations, the default values,
/// the equality and the order a generic definition uses against .NET's own, the verifier's rules of
/// origins and open types, and the substitution of a template.
/// </summary>
[TestFixture]
public sealed class IrGenericsTests {

  private static IReadOnlyList<string> Problems(IrModule module) => [.. IrVerifier.Verify(module).Select(diagnostic => diagnostic.Message)];

  /// <summary>A function of two parameters of one type that returns one node over them.</summary>
  private static IrFunction Over(string name, IrType type, IrType result, Func<IrExpr, IrExpr, IrExpr> body) {
    var (a, b) = (new IrLocal("a", type), new IrLocal("b", type));
    return new IrFunction(name, true, result, [a, b], [], new IrBlock([new IrReturn(body(new IrLocalRef(a), new IrLocalRef(b)))]));
  }

  [Test]
  public void Names_OfInstantiations() {
    Assert.That(IrGenerics.Mangle("IndexOf", [IrType.F64]), Is.EqualTo("IndexOf<f64>"));
    Assert.That(IrGenerics.Mangle("Pair", [IrType.I32, IrType.Array(IrType.U8)]), Is.EqualTo("Pair<i32,u8[]>"));
    Assert.That(IrGenerics.SourceName("Ring<i64>"), Is.EqualTo("Ring"));
    Assert.That(IrGenerics.SourceName("Ring<i64>.Push"), Is.EqualTo("Ring"));
    Assert.That(IrGenerics.SourceName("plain"), Is.EqualTo("plain"));
    Assert.That(IrGenerics.IsInstance("middle<i32[]>"), Is.True);
    Assert.That(IrGenerics.IsInstance("Ring<i64>.Push"), Is.False);
    Assert.That(IrGenerics.IsInstance("plain"), Is.False);
    var origin = new IrGenericOrigin("Pair", ["K", "V"], [IrType.I32, IrType.F64], []);
    Assert.That(origin.Display, Is.EqualTo("Pair<K, V>"));
  }

  [Test]
  public void DefaultOf_EveryKindOfType() {
    var record = new IrStructType("S");
    record.Define([new IrField("x", IrType.I32, false)]);
    var type = new IrClassType("C");
    Assert.That(IrGenerics.DefaultOf(IrType.I64), Is.EqualTo(new IrConst(IrType.I64, 0)));
    Assert.That(IrGenerics.DefaultOf(IrType.F32), Is.EqualTo(new IrConst(IrType.F32, 0)));
    Assert.That(IrGenerics.DefaultOf(IrType.Bool), Is.EqualTo(new IrConst(IrType.Bool, 0)));
    Assert.That(IrGenerics.DefaultOf(IrType.Struct(record)), Is.EqualTo(new IrStructDefault(IrType.Struct(record))));
    Assert.That(IrGenerics.DefaultOf(IrType.Object(type)), Is.EqualTo(new IrNull(IrType.Object(type))));
    Assert.Throws<ArgumentException>(() => IrGenerics.DefaultOf(IrType.String));
    Assert.Throws<ArgumentException>(() => IrGenerics.DefaultOf(IrType.Array(IrType.I32)));
  }

  [Test]
  public void EquatesAndOrders_TheTypesTheyAreDefinedOn() {
    var type = IrType.Object(new IrClassType("C"));
    Assert.That(new[] { IrType.I8, IrType.U64, IrType.F32, IrType.Bool, type }.All(IrGenerics.Equates), Is.True);
    Assert.That(new[] { IrType.String, IrType.Array(IrType.I32) }.Any(IrGenerics.Equates), Is.False);
    Assert.That(new[] { IrType.I8, IrType.U64, IrType.F64, IrType.Bool }.All(IrGenerics.Orders), Is.True);
    Assert.That(new[] { type, IrType.String }.Any(IrGenerics.Orders), Is.False);
  }

  /// <summary>The values of each scalar type the equality and the order are run over: the extremes, zero, and for floating point the signed zeros, the infinities and NaN.</summary>
  private static IEnumerable<TestCaseData> Scalars() {
    IEnumerable<TestCaseData> Of<T>(IrType type, params T[] values) where T : IEquatable<T>, IComparable<T>
      => [new TestCaseData(type, values.Cast<object>().ToArray(), (Func<object, object, bool>)((a, b) => ((T)a).Equals((T)b)), (Func<object, object, int>)((a, b) => ((T)a).CompareTo((T)b))).SetName(type.ToString())];

    return [
      .. Of<sbyte>(IrType.I8, sbyte.MinValue, -1, 0, 1, sbyte.MaxValue),
      .. Of<byte>(IrType.U8, 0, 1, 200, byte.MaxValue),
      .. Of<short>(IrType.I16, short.MinValue, -2, 0, short.MaxValue),
      .. Of<ushort>(IrType.U16, 0, 7, ushort.MaxValue),
      .. Of(IrType.I32, int.MinValue, -1, 0, 1, int.MaxValue),
      .. Of(IrType.U32, 0u, 1u, uint.MaxValue),
      .. Of(IrType.I64, long.MinValue, -1L, 0L, (1L << 53) + 1, long.MaxValue),
      .. Of(IrType.U64, 0ul, 1ul, ulong.MaxValue),
      .. Of(IrType.F32, float.NaN, float.NegativeInfinity, -0.0f, 0.0f, 1.5f, float.PositiveInfinity),
      .. Of(IrType.F64, double.NaN, double.NegativeInfinity, -0.0, 0.0, 2.5, double.PositiveInfinity),
      .. Of(IrType.Bool, false, true)
    ];
  }

  /// <summary>Test design 2, 5 and 6: the interpreter's equality and order of every scalar type are .NET's Equals and CompareTo of it, NaN and -0.0 included.</summary>
  [TestCaseSource(nameof(Scalars))]
  public void EqualityAndOrder_AreThoseOfDotNet(IrType type, object[] values, Func<object, object, bool> equals, Func<object, object, int> compare) {
    var module = new IrModule("M", [
      Over("Eq", type, IrType.Bool, (a, b) => new IrGenericEquals(a, b)),
      Over("Cmp", type, IrType.I32, (a, b) => new IrGenericCompare(a, b))
    ]);
    Assert.That(Problems(module), Is.Empty);
    foreach (var left in values)
      foreach (var right in values) {
        Assert.That(IrInterpreter.Call(module, "Eq", [left, right]), Is.EqualTo(equals(left, right)), $"{left} Equals {right}");
        Assert.That(IrInterpreter.Call(module, "Cmp", [left, right]), Is.EqualTo(compare(left, right)), $"{left} CompareTo {right}");
      }
  }

  /// <summary>References are equal by identity, a record by its fields; two nulls are equal.</summary>
  [TestCase(false, false)]
  [TestCase(true, true)]
  public void Equality_OfReferences(bool record, bool sameFields) {
    var type = new IrClassType("C") { IsRecord = record };
    var field = new IrField("v", IrType.I32, false);
    var constructor = new IrConstructor("C.ctor");
    type.Define([field]);
    type.DefineMembers([constructor], [], [], []);
    var self = new IrLocal("this", IrType.Object(type));
    var value = new IrLocal("v", IrType.I32);
    IrExpr New(long v) => new IrNew(IrType.Object(type), constructor, [new IrConst(IrType.I32, v)]);
    var module = new IrModule("M", [
      new IrFunction("C.ctor", false, IrType.Void, [self, value], [], new IrBlock([new IrFieldStore(new IrLocalRef(self), field, new IrLocalRef(value))])),
      new IrFunction("Two", true, IrType.Bool, [], [], new IrBlock([new IrReturn(new IrGenericEquals(New(1), New(1)))])),
      new IrFunction("Nulls", true, IrType.Bool, [], [], new IrBlock([new IrReturn(new IrGenericEquals(new IrNull(IrType.Object(type)), new IrNull(IrType.Object(type))))]))
    ]) { Types = [type] };
    Assert.That(Problems(module), Is.Empty);
    Assert.That(IrInterpreter.Call(module, "Two", []), Is.EqualTo(sameFields));
    Assert.That(IrInterpreter.Call(module, "Nulls", []), Is.EqualTo(true));
  }

  /// <summary>Test design 11: the verifier refuses an origin that does not match its instantiation, a constraint of no parameter, an open type and ill-typed operands.</summary>
  [Test]
  public void Verifier_RefusesBrokenGenerics() {
    void Refused(IrModule module, string message) => Assert.That(Problems(module), Has.Some.Contains(message), string.Join("\n", Problems(module)));
    var standIn = new IrInterfaceType("T");
    standIn.DefineMembers([], [], []);
    var open = IrType.Object(standIn);
    var template = new IrGenericTemplate(Over("Id<T>", open, open, (a, _) => a), [open]);
    var origin = new IrGenericOrigin("Id", ["T"], [IrType.I32], [new IrGenericConstraint("T", IrGenericConstraintKind.Struct)]) { Template = template };
    var good = Over("Id<i32>", IrType.I32, IrType.I32, (a, _) => a) with { Origin = origin };
    Assert.That(Problems(new IrModule("M", [good])), Is.Empty);

    Refused(new IrModule("M", [good with { Name = "Id<f64>" }]), "do not match the instantiation");
    Refused(new IrModule("M", [good with { Origin = origin with { TypeArguments = [IrType.I32, IrType.I32] } }]), "do not match the instantiation");
    Refused(new IrModule("M", [good with { Origin = origin with { Constraints = [new IrGenericConstraint("U", IrGenericConstraintKind.Class)] } }]), "which is no type parameter of Id<T>");
    Refused(new IrModule("M", [good, Over("Leak", open, IrType.Bool, (a, b) => new IrConst(IrType.Bool, 1))]) { Types = [standIn] }, "the type T is open: a type parameter of Id<T>");
    Refused(new IrModule("M", [Over("E", IrType.String, IrType.Bool, (a, b) => new IrGenericEquals(a, b))]), "are compared for equality as a generic definition does");
    Refused(new IrModule("M", [Over("E", IrType.I32, IrType.Bool, (a, b) => new IrGenericEquals(a, new IrConst(IrType.I64, 1)))]), "values of the types i32 and i64");
    var type = new IrClassType("C");
    type.Define([]);
    type.DefineMembers([], [], [], []);
    Refused(new IrModule("M", [Over("O", IrType.Object(type), IrType.I32, (a, b) => new IrGenericCompare(a, b))]) { Types = [type] }, "are ordered as a generic definition does");
  }

  /// <summary>A template instantiated: every type, the calls it explains, default(T) and new T() follow the argument.</summary>
  [Test]
  public void Instantiate_ReplacesTheParameters() {
    var standIn = new IrInterfaceType("T");
    standIn.DefineMembers([], [], []);
    var open = IrType.Object(standIn);
    var box = new IrClassType("Box");
    box.Define([]);
    var ctor = new IrConstructor("Box.ctor");
    box.DefineMembers([ctor], [], [], []);
    var self = new IrLocal("this", IrType.Object(box));
    var boxCtor = new IrFunction("Box.ctor", false, IrType.Void, [self], [], new IrBlock([]));
    var items = new IrLocal("a", IrType.Array(open));
    var made = new IrLocal("m", open);
    var body = new IrBlock([
      new IrDeclare(made, new IrNew(open, IrGenerics.Parameterless, [])),
      new IrAssign(made, new IrNull(open)),
      new IrExprStmt(new IrCall("Other<T>", IrType.Void, [new IrLocalRef(items)])),
      new IrReturn(new IrGenericEquals(new IrArrayLoad(new IrLocalRef(items), new IrConst(IrType.I32, 0)), new IrLocalRef(made)))
    ]);
    var template = new IrGenericTemplate(new IrFunction("F<T>", true, IrType.Bool, [items], [made], body), [open]) {
      Calls = new Dictionary<string, IrGenericCall> { ["Other<T>"] = new("Other", [open]) }
    };
    var module = new IrModule("M", [boxCtor]) { Types = [box] };
    var numbers = IrGenerics.Instantiate(template, [IrType.F64], "F<f64>", module);
    Assert.That(numbers.Name, Is.EqualTo("F<f64>"));
    Assert.That(numbers.Parameters[0].Type, Is.EqualTo(IrType.Array(IrType.F64)));
    Assert.That(numbers.Body.Statements[0], Is.EqualTo(new IrDeclare(numbers.Locals[0], new IrConst(IrType.F64, 0))));
    Assert.That(((IrAssign)numbers.Body.Statements[1]).Value, Is.EqualTo(new IrConst(IrType.F64, 0)));
    Assert.That(((IrCall)((IrExprStmt)numbers.Body.Statements[2]).Expression).Function, Is.EqualTo("Other<f64>"));
    var boxes = IrGenerics.Instantiate(template, [IrType.Object(box)], "F<Box>", module);
    Assert.That(((IrDeclare)boxes.Body.Statements[0]).Initializer, Is.EqualTo(new IrNew(IrType.Object(box), ctor, [])).Using<IrExpr>(Same));
    Assert.That(((IrAssign)boxes.Body.Statements[1]).Value, Is.EqualTo(new IrNull(IrType.Object(box))));

    Assert.Throws<ArgumentException>(() => IrGenerics.Instantiate(template, [], "F<>", module));
    var empty = new IrClassType("Empty");
    empty.Define([]);
    empty.DefineMembers([], [], [], []);
    Assert.Throws<ArgumentException>(() => IrGenerics.Instantiate(template, [IrType.Object(empty)], "F<Empty>", module));
  }

  private static bool Same(IrExpr left, IrExpr right) => left is IrNew a && right is IrNew b && a.Type == b.Type && a.Constructor == b.Constructor && a.Arguments.Count == b.Arguments.Count;

  /// <summary>Every node kind survives an instantiation that replaces nothing, so that the instantiation is equivalent to the template.</summary>
  [Test]
  public void Instantiate_WithoutParameters_KeepsEveryNode() {
    var record = new IrStructType("S");
    var x = new IrField("x", IrType.I32, false);
    record.Define([x]);
    var type = new IrClassType("C");
    var field = new IrField("f", IrType.I32, false);
    type.Define([field]);
    var constructor = new IrConstructor("C.ctor");
    var method = new IrMethod("M", "C.M", false);
    type.DefineMembers([constructor], [method], [], []);
    var global = new IrGlobal("g", IrType.I32, null);
    var constant = new IrModuleConst("k", IrType.I32, [3]);
    var i = new IrLocal("i", IrType.I32);
    var s = new IrLocal("s", IrType.Struct(record));
    var o = new IrLocal("o", IrType.Object(type));
    var d = new IrLocal("d", IrType.F64);
    var array = new IrLocal("a", IrType.Array(IrType.I32));
    IrExpr I() => new IrLocalRef(i);
    var body = new IrBlock([
      new IrDeclare(i, new IrBinary(IrBinaryOp.Add, new IrUnary(IrUnaryOp.Negate, new IrConstRef(constant)), new IrGlobalRef(global))),
      new IrDeclare(d, new IrConvert(IrType.F64, I())),
      new IrDeclare(array, new IrNewArray(IrType.Array(IrType.I32), new IrArrayLength(new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.I32), [I()])))),
      new IrDeclare(s, new IrStructNew(IrType.Struct(record), [I()])),
      new IrAssign(s, new IrStructDefault(IrType.Struct(record))),
      new IrDeclare(o, new IrNew(IrType.Object(type), constructor, [])),
      new IrAssign(i, new IrConditional(new IrStructEquals(new IrLocalRef(s), new IrLocalRef(s)), new IrFieldLoad(new IrLocalRef(s), x), new IrStep(I(), true, true, false))),
      new IrGlobalAssign(global, new IrArrayLoad(new IrLocalRef(array), I())),
      new IrStore(new IrLocalRef(array), I(), new IrCall("F", IrType.I32, [])),
      new IrFieldStore(new IrLocalRef(o), field, new IrMethodCall(IrType.I32, method, new IrLocalRef(o), [])),
      new IrExprStmt(new IrReferenceEquals(new IrLocalRef(o), new IrAsCast(IrType.Object(type), new IrCast(IrType.Object(type), new IrLocalRef(o))))),
      new IrExprStmt(new IrTypeTest(new IrLocalRef(o), IrType.Object(type))),
      new IrExprStmt(new IrGenericCompare(I(), I())),
      new IrExprStmt(new IrStrConst("a")),
      new IrIf(new IrConst(IrType.Bool, 1), new IrBlock([new IrBreak()]), new IrBlock([new IrContinue()])),
      new IrWhile(new IrConst(IrType.Bool, 0), new IrBlock([])),
      new IrDoWhile(new IrBlock([]), new IrConst(IrType.Bool, 0)),
      new IrFor(new IrConst(IrType.Bool, 0), new IrBlock([]), new IrBlock([])),
      new IrSwitch(I(), [new IrSwitchSection([1], true, new IrBlock([]))]),
      new IrReturn(null)
    ]);
    var function = new IrFunction("F", true, IrType.Void, [], [i, s, o, d, array], body);
    var template = new IrGenericTemplate(function, []);
    var copy = IrGenerics.Instantiate(template, [], "F", new IrModule("M", [function]));
    Assert.That(IrGenerics.Equivalent(copy, function), Is.True);
    Assert.That(IrGenerics.Equivalent(copy with { Name = "G" }, function), Is.False);
    Assert.That(IrGenerics.Equivalent(copy with { Body = new IrBlock([.. body.Statements.Take(3)]) }, function), Is.False);
    Assert.That(IrGenerics.Equivalent(copy with { Body = new IrBlock([new IrReturn(new IrConst(IrType.I32, 1))]) }, function with { Body = new IrBlock([new IrReturn(new IrConst(IrType.I32, 2))]) }), Is.False);
    Assert.That(IrGenerics.Equivalent(copy with { Body = new IrBlock([new IrReturn(new IrConst(IrType.I32, 1))]) }, function with { Body = new IrBlock([new IrReturn(null)]) }), Is.False);
    Assert.That(IrGenerics.Equivalent(copy with { Body = new IrBlock([new IrExprStmt(new IrConst(IrType.I32, 1))]) }, function with { Body = new IrBlock([new IrExprStmt(new IrLocalRef(i))]) }), Is.False);
  }

  /// <summary>A rename reaches the functions, the calls in their bodies and the calls in the initializers of the globals.</summary>
  [Test]
  public void Rename_ReachesEveryCall() {
    var global = new IrGlobal("g", IrType.I32, new IrCall("a<1>", IrType.I32, []));
    var plain = new IrGlobal("h", IrType.I32, null);
    var module = new IrModule("M", [
      new IrFunction("a<1>", false, IrType.I32, [], [], new IrBlock([new IrReturn(new IrConst(IrType.I32, 4))])),
      new IrFunction("F", true, IrType.I32, [], [], new IrBlock([new IrGlobalAssign(plain, new IrGlobalRef(global)), new IrReturn(new IrBinary(IrBinaryOp.Add, new IrCall("a<1>", IrType.I32, []), new IrGlobalRef(plain)))]))
    ]) { Globals = [global, plain] };
    var renamed = IrGenerics.Rename(module, new Dictionary<string, string> { ["a<1>"] = "a<i32>" });
    Assert.That(renamed.Functions.Select(function => function.Name), Is.EqualTo(new[] { "a<i32>", "F" }));
    Assert.That(((IrCall)renamed.Globals[0].Initializer!).Function, Is.EqualTo("a<i32>"));
    Assert.That(Problems(renamed), Is.Empty);
    Assert.That(IrInterpreter.Call(renamed, "F", []), Is.EqualTo(8));
  }

}
