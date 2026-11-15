using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Collections;

/// <summary>
/// Lists, maps and sets in the IR on modules built by hand: the types, the rules of the verifier and
/// what the interpreter does - both orders, both modes of enumeration, the key equality and the traps.
/// </summary>
[TestFixture]
public sealed class IrCollectionsTests {

  private static readonly IrType IntList = IrType.List(IrType.I32);

  private static IrFunction F(IrType result, IReadOnlyList<IrLocal> locals, params IrStmt[] statements)
    => new("F", true, result, [], locals, new IrBlock(statements));

  private static IrModule Module(params IrFunction[] functions) => new("M", functions);

  private static IReadOnlyList<string> Problems(IrModule module) => [.. IrVerifier.Verify(module).Select(diagnostic => diagnostic.Message)];

  private static IrConst I(long value) => new(IrType.I32, value);

  private static IrConst D(double value) => new(IrType.F64, IrOperators.Bits(IrType.F64, value));

  private static IrLocalRef R(IrLocal local) => new(local);

  private static IrCollection Op(IrCollectionOp op, IrType type, params IrExpr[] arguments) => new(op, type, arguments);

  private static IrExprStmt Do(IrCollectionOp op, params IrExpr[] arguments) => new(Op(op, IrType.Void, arguments));

  private static IrStmt Times10Plus(IrLocal sum, IrExpr value)
    => new IrAssign(sum, new IrBinary(IrBinaryOp.Add, new IrBinary(IrBinaryOp.Multiply, R(sum), I(10)), value));

  private static object? Run(IrType result, IReadOnlyList<IrLocal> locals, params IrStmt[] statements) {
    var module = Module(F(result, locals, statements));
    Assert.That(Problems(module), Is.Empty);
    return IrInterpreter.Call(module, "F", []);
  }

  private static IrTrap Trap(IrType result, IReadOnlyList<IrLocal> locals, params IrStmt[] statements)
    => Assert.Throws<IrTrapException>(() => Run(result, locals, statements))!.Trap;

  [Test]
  public void Types_NameElementsKeysAndOrder() {
    var map = IrType.Map(IrType.String, IrType.F64, MapOrder.SlotReuse);
    var set = IrType.Set(IrType.I64, MapOrder.Insertion);
    Assert.That(IntList.ToString(), Is.EqualTo("list<i32>"));
    Assert.That(map.ToString(), Is.EqualTo("map<str, f64, slotreuse>"));
    Assert.That(set.ToString(), Is.EqualTo("set<i64, insertion>"));
    Assert.That(map.Key, Is.EqualTo(IrType.String));
    Assert.That(map.Element, Is.EqualTo(IrType.F64));
    Assert.That(map, Is.Not.EqualTo(IrType.Map(IrType.String, IrType.F64, MapOrder.Insertion)));
    Assert.That(IntList.IsCollection && IntList.IsList && map.IsCollection && !map.IsList && !IrType.I32.IsCollection, Is.True);
    Assert.That(IntList.IsStorable && map.IsStorable && set.IsStorable, Is.True);
  }

  [Test]
  public void Types_RefuseViewsOptionalsAndStructKeys() {
    var point = new IrStructType("P");
    point.Define([new IrField("X", IrType.I32, false)]);
    Assert.That(IrType.List(IrType.Struct(point)).IsStorable, Is.True);
    Assert.That(IrType.Map(IrType.I32, IrType.Struct(point), MapOrder.Insertion).IsStorable, Is.True);
    Assert.That(IrType.Map(IrType.Struct(point), IrType.I32, MapOrder.Insertion).IsStorable, Is.False);
    Assert.That(IrType.Set(IrType.Struct(point), MapOrder.Insertion).IsStorable, Is.False);
    Assert.That(IrType.List(IrType.Span(IrType.U8)).IsStorable, Is.False);
    Assert.That(IrType.List(IrType.Optional(IrType.I32)).IsStorable, Is.False);
    Assert.That(IrType.List(IrType.Void).IsStorable, Is.False);
    Assert.That(IrType.List(IntList).IsStorable, Is.True);
    Assert.That(IrType.Set(IntList, MapOrder.Insertion).IsStorable, Is.False);
  }

  [Test]
  public void Absent_IsOptionalForScalarsAndTheReferenceItself() {
    Assert.That(IrCollections.Absent(IrType.I32), Is.EqualTo(IrType.Optional(IrType.I32)));
    Assert.That(IrCollections.Absent(IrType.String), Is.Null);
    var type = new IrClassType("C");
    type.Define([]);
    Assert.That(IrCollections.Absent(IrType.Object(type)), Is.EqualTo(IrType.Object(type)));
  }

  [TestCase(IrCollectionOp.ListAdd, true), TestCase(IrCollectionOp.ListGet, false), TestCase(IrCollectionOp.MapPut, true),
   TestCase(IrCollectionOp.MapRemove, false), TestCase(IrCollectionOp.MapClear, false), TestCase(IrCollectionOp.SetAdd, true), TestCase(IrCollectionOp.SetRemove, false)]
  public void Invalidates_ListChangesAndInsertionsOfKeys(IrCollectionOp op, bool invalidates) {
    Assert.That(IrCollections.Invalidates(op), Is.EqualTo(invalidates));
    Assert.That(IrCollections.Mutates(op), Is.EqualTo(invalidates || op is IrCollectionOp.MapRemove or IrCollectionOp.MapClear or IrCollectionOp.SetRemove));
  }

  /// <summary>Test design 1, lists: add, insert, set, remove and count as C# computes 7233.</summary>
  [Test]
  public void ListOperations_ChangeTheElements() {
    var list = new IrLocal("l", IntList);
    var result = Run(IrType.I32, [list],
      new IrDeclare(list, Op(IrCollectionOp.ListNew, IntList)),
      Do(IrCollectionOp.ListAdd, R(list), I(3)),
      Do(IrCollectionOp.ListAdd, R(list), I(2)),
      Do(IrCollectionOp.ListInsert, R(list), I(0), I(7)),
      Do(IrCollectionOp.ListInsert, R(list), I(3), I(9)),
      Do(IrCollectionOp.ListSet, R(list), I(3), I(3)),
      Do(IrCollectionOp.ListRemoveAt, R(list), I(1)),
      Do(IrCollectionOp.ListAdd, R(list), I(3)),
      new IrReturn(new IrBinary(IrBinaryOp.Add,
        new IrBinary(IrBinaryOp.Multiply, Op(IrCollectionOp.ListGet, IrType.I32, R(list), I(0)), I(1000)),
        new IrBinary(IrBinaryOp.Add, new IrBinary(IrBinaryOp.Multiply, Op(IrCollectionOp.ListGet, IrType.I32, R(list), I(1)), I(100)),
          new IrBinary(IrBinaryOp.Add, new IrBinary(IrBinaryOp.Multiply, Op(IrCollectionOp.ListCount, IrType.I32, R(list)), I(0)),
            new IrBinary(IrBinaryOp.Add, new IrBinary(IrBinaryOp.Multiply, Op(IrCollectionOp.ListGet, IrType.I32, R(list), I(2)), I(10)), Op(IrCollectionOp.ListGet, IrType.I32, R(list), I(3))))))));
    Assert.That(result, Is.EqualTo(7233));
  }

  /// <summary>Test design 7: indices -1 and count trap, 0 and count - 1 are elements; insertion takes count.</summary>
  [TestCase(IrCollectionOp.ListGet, -1, true), TestCase(IrCollectionOp.ListGet, 0, false), TestCase(IrCollectionOp.ListGet, 1, false), TestCase(IrCollectionOp.ListGet, 2, true),
   TestCase(IrCollectionOp.ListSet, 2, true), TestCase(IrCollectionOp.ListSet, -1, true), TestCase(IrCollectionOp.ListRemoveAt, 2, true), TestCase(IrCollectionOp.ListRemoveAt, 1, false),
   TestCase(IrCollectionOp.ListInsert, 2, false), TestCase(IrCollectionOp.ListInsert, 3, true), TestCase(IrCollectionOp.ListInsert, -1, true)]
  public void ListIndices_TrapOutsideTheList(IrCollectionOp op, int index, bool traps) {
    var list = new IrLocal("l", IntList);
    IrStmt operation = op switch {
      IrCollectionOp.ListGet => new IrExprStmt(Op(op, IrType.I32, R(list), I(index))),
      IrCollectionOp.ListRemoveAt => Do(op, R(list), I(index)),
      _ => Do(op, R(list), I(index), I(5))
    };
    IrStmt[] body = [
      new IrDeclare(list, Op(IrCollectionOp.ListFromArray, IntList, new IrNewArray(IrType.Array(IrType.I32), I(2)))),
      operation,
      new IrReturn(Op(IrCollectionOp.ListCount, IrType.I32, R(list)))
    ];
    if (traps)
      Assert.That(Trap(IrType.I32, [list], body), Is.EqualTo(IrTrap.ArgumentOutOfRange));
    else
      Assert.That(Run(IrType.I32, [list], body), Is.InstanceOf<int>());
  }

  /// <summary>Test design 6: pop of an empty list is none, of a list of objects null; clear empties.</summary>
  [Test]
  public void RemoveLast_IsNoneOnAnEmptyList() {
    var list = new IrLocal("l", IntList);
    var optional = IrType.Optional(IrType.I32);
    var result = Run(IrType.I32, [list],
      new IrDeclare(list, Op(IrCollectionOp.ListNew, IntList)),
      Do(IrCollectionOp.ListAdd, R(list), I(4)),
      new IrReturn(new IrBinary(IrBinaryOp.Add,
        new IrCoalesce(Op(IrCollectionOp.ListRemoveLast, optional, R(list)), I(100)),
        new IrCoalesce(Op(IrCollectionOp.ListRemoveLast, optional, R(list)), I(10)))));
    Assert.That(result, Is.EqualTo(14));

    var type = new IrClassType("C");
    type.Define([]);
    type.DefineMembers([], [], [], []);
    var objects = IrType.List(IrType.Object(type));
    var bag = new IrLocal("b", objects);
    var module = new IrModule("M", [F(IrType.Bool, [bag],
      new IrDeclare(bag, Op(IrCollectionOp.ListNew, objects)),
      Do(IrCollectionOp.ListClear, R(bag)),
      new IrReturn(new IrIsNull(Op(IrCollectionOp.ListRemoveLast, IrType.Object(type), R(bag)))))], []) { Types = [type] };
    Assert.That(Problems(module), Is.Empty);
    Assert.That(IrInterpreter.Call(module, "F", []), Is.True);
  }

  /// <summary>Test design 5: on [NaN, -0.0], IndexOf finds NaN at 0, indexOf finds nothing for NaN and 0 at 1, includes finds NaN.</summary>
  [TestCase(IrCollectionOp.ListIndexOfEquals, double.NaN, 0), TestCase(IrCollectionOp.ListIndexOfEquals, 0.0, 1),
   TestCase(IrCollectionOp.ListIndexOfStrict, double.NaN, -1), TestCase(IrCollectionOp.ListIndexOfStrict, 0.0, 1),
   TestCase(IrCollectionOp.ListContains, double.NaN, 1), TestCase(IrCollectionOp.ListIncludesSameValueZero, double.NaN, 1),
   TestCase(IrCollectionOp.ListIncludesSameValueZero, 0.0, 1), TestCase(IrCollectionOp.ListContains, 2.0, 0)]
  public void Search_FollowsEachLanguage(IrCollectionOp op, double value, int expected) {
    var type = IrType.List(IrType.F64);
    var list = new IrLocal("l", type);
    IrExpr search = op is IrCollectionOp.ListIndexOfEquals or IrCollectionOp.ListIndexOfStrict
      ? Op(op, IrType.I32, R(list), D(value))
      : new IrConditional(Op(op, IrType.Bool, R(list), D(value)), I(1), I(0));
    var result = Run(IrType.I32, [list],
      new IrDeclare(list, Op(IrCollectionOp.ListNew, type)),
      Do(IrCollectionOp.ListAdd, R(list), D(double.NaN)),
      Do(IrCollectionOp.ListAdd, R(list), D(-0.0)),
      new IrReturn(search));
    Assert.That(result, Is.EqualTo(expected));
  }

  [Test]
  public void CopyToArray_AndFromArray_CopyTheElements() {
    var list = new IrLocal("l", IntList);
    var array = new IrLocal("a", IrType.Array(IrType.I32));
    var result = Run(IrType.I32, [list, array],
      new IrDeclare(list, Op(IrCollectionOp.ListNew, IntList)),
      Do(IrCollectionOp.ListAdd, R(list), I(5)),
      new IrDeclare(array, Op(IrCollectionOp.ListCopyToArray, IrType.Array(IrType.I32), R(list))),
      Do(IrCollectionOp.ListSet, R(list), I(0), I(6)),
      new IrReturn(new IrBinary(IrBinaryOp.Add, new IrArrayLoad(R(array), I(0)), new IrArrayLength(R(array)))));
    Assert.That(result, Is.EqualTo(6));
  }

  /// <summary>Struct elements are copied on the way in and out, as C# copies them.</summary>
  [Test]
  public void StructElements_AreCopied() {
    var point = new IrStructType("P");
    var x = new IrField("X", IrType.I32, false);
    point.Define([x]);
    var type = IrType.List(IrType.Struct(point));
    var list = new IrLocal("l", type);
    var p = new IrLocal("p", IrType.Struct(point));
    var module = new IrModule("M", [F(IrType.I32, [list, p],
      new IrDeclare(list, Op(IrCollectionOp.ListNew, type)),
      new IrDeclare(p, new IrStructNew(IrType.Struct(point), [I(1)])),
      Do(IrCollectionOp.ListAdd, R(list), R(p)),
      new IrFieldStore(R(p), x, I(2)),
      new IrAssign(p, Op(IrCollectionOp.ListGet, IrType.Struct(point), R(list), I(0))),
      new IrFieldStore(R(p), x, I(3)),
      new IrReturn(new IrFieldLoad(Op(IrCollectionOp.ListGet, IrType.Struct(point), R(list), I(0)), x)))], []) { Types = [point] };
    Assert.That(Problems(module), Is.Empty);
    Assert.That(IrInterpreter.Call(module, "F", []), Is.EqualTo(1));
  }

  private static IrStmt[] Insert(IrLocal map, params long[] keys)
    => [.. keys.Select(key => (IrStmt)Do(IrCollectionOp.MapPut, R(map), I(key), I(key)))];

  private static IrStmt[] Remove(IrLocal map, params long[] keys)
    => [.. keys.Select(key => (IrStmt)new IrExprStmt(Op(IrCollectionOp.MapRemove, IrType.Bool, R(map), I(key))))];

  private static IrStmt Digits(IrLocal map, IrLocal key, IrLocal sum, EnumerationMode mode)
    => new IrForEachCollection(R(map), mode, key, null, new IrBlock([Times10Plus(sum, R(key))]));

  /// <summary>Test design 2: add 1, 2, 3, remove 1 and 3, add 4, 5, 6 enumerates 5246 in slot order and 2456 in insertion order.</summary>
  [TestCase(MapOrder.SlotReuse, 5246), TestCase(MapOrder.Insertion, 2456)]
  public void MapOrder_FollowsTheSourceLanguage(MapOrder order, int expected) {
    var type = IrType.Map(IrType.I32, IrType.I32, order);
    var map = new IrLocal("m", type);
    var key = new IrLocal("k", IrType.I32);
    var sum = new IrLocal("s", IrType.I32);
    var result = Run(IrType.I32, [map, key, sum], [
      new IrDeclare(map, Op(IrCollectionOp.MapNew, type)),
      new IrDeclare(sum, I(0)),
      .. Insert(map, 1, 2, 3), .. Remove(map, 1, 3, 7), .. Insert(map, 4, 5, 6),
      Digits(map, key, sum, EnumerationMode.Checked),
      new IrReturn(R(sum))]);
    Assert.That(result, Is.EqualTo(expected));
  }

  /// <summary>Test design 2: after a clear a slot-reuse map starts at the first slot again; both orders enumerate 98.</summary>
  [TestCase(MapOrder.SlotReuse), TestCase(MapOrder.Insertion)]
  public void Clear_ThenInsert_StartsAfresh(MapOrder order) {
    var type = IrType.Set(IrType.I32, order);
    var set = new IrLocal("s", type);
    var key = new IrLocal("k", IrType.I32);
    var sum = new IrLocal("n", IrType.I32);
    var result = Run(IrType.I32, [set, key, sum],
      new IrDeclare(set, Op(IrCollectionOp.SetNew, type)),
      new IrDeclare(sum, I(0)),
      new IrExprStmt(Op(IrCollectionOp.SetAdd, IrType.Bool, R(set), I(1))),
      new IrExprStmt(Op(IrCollectionOp.SetAdd, IrType.Bool, R(set), I(2))),
      new IrExprStmt(Op(IrCollectionOp.SetRemove, IrType.Bool, R(set), I(1))),
      Do(IrCollectionOp.SetClear, R(set)),
      new IrExprStmt(Op(IrCollectionOp.SetAdd, IrType.Bool, R(set), I(9))),
      new IrExprStmt(Op(IrCollectionOp.SetAdd, IrType.Bool, R(set), I(8))),
      new IrForEachCollection(R(set), EnumerationMode.Checked, key, null, new IrBlock([Times10Plus(sum, R(key))])),
      new IrReturn(new IrBinary(IrBinaryOp.Add, R(sum), new IrBinary(IrBinaryOp.Multiply, Op(IrCollectionOp.SetCount, IrType.I32, R(set)), I(0)))));
    Assert.That(result, Is.EqualTo(98));
  }

  /// <summary>Test design 3: -0 then +0 keeps the first key; every NaN is one key.</summary>
  [TestCase(-0.0, 0.0, double.NegativeInfinity), TestCase(0.0, -0.0, double.PositiveInfinity)]
  public void FloatKeys_KeepThePresentKey(double first, double second, double expected) {
    var type = IrType.Map(IrType.F64, IrType.I32, MapOrder.SlotReuse);
    var map = new IrLocal("m", type);
    var key = new IrLocal("k", IrType.F64);
    var value = new IrLocal("v", IrType.I32);
    var result = new IrLocal("r", IrType.F64);
    var answer = Run(IrType.F64, [map, key, value, result],
      new IrDeclare(map, Op(IrCollectionOp.MapNew, type)),
      new IrDeclare(result, D(0)),
      Do(IrCollectionOp.MapPut, R(map), D(first), I(1)),
      Do(IrCollectionOp.MapPut, R(map), D(second), I(2)),
      new IrForEachCollection(R(map), EnumerationMode.Checked, key, value,
        new IrBlock([new IrAssign(result, new IrBinary(IrBinaryOp.Divide, D(1), R(key)))])),
      new IrReturn(new IrBinary(IrBinaryOp.Add, R(result), new IrConvert(IrType.F64, new IrBinary(IrBinaryOp.Subtract, Op(IrCollectionOp.MapGet, IrType.I32, R(map), D(0)), I(2)), IrConversion.Wrap))));
    Assert.That(answer, Is.EqualTo(expected));
  }

  [Test]
  public void NaNKeys_AreOneKey() {
    var type = IrType.Map(IrType.F64, IrType.I32, MapOrder.Insertion);
    var map = new IrLocal("m", type);
    var result = Run(IrType.I32, [map],
      new IrDeclare(map, Op(IrCollectionOp.MapNew, type)),
      Do(IrCollectionOp.MapPut, R(map), D(double.NaN), I(1)),
      Do(IrCollectionOp.MapPut, R(map), new IrBinary(IrBinaryOp.Divide, D(0), D(0)), I(2)),
      new IrReturn(new IrBinary(IrBinaryOp.Add, new IrBinary(IrBinaryOp.Multiply, Op(IrCollectionOp.MapCount, IrType.I32, R(map)), I(10)),
        Op(IrCollectionOp.MapGet, IrType.I32, R(map), D(double.NaN)))));
    Assert.That(result, Is.EqualTo(12));
  }

  /// <summary>Test design 3: string keys by value, object keys by identity, bool and i64 keys by value.</summary>
  [Test]
  public void KeyTypes_CompareAsTheRuleSays() {
    var type = new IrClassType("C");
    type.Define([]);
    var constructor = new IrConstructor("C..ctor");
    type.DefineMembers([constructor], [], [], []);
    var body = new IrFunction("C..ctor", false, IrType.Void, [new IrLocal("this", IrType.Object(type))], [], new IrBlock([]));
    var objects = IrType.Set(IrType.Object(type), MapOrder.Insertion);
    var strings = IrType.Set(IrType.String, MapOrder.SlotReuse);
    var longs = IrType.Set(IrType.I64, MapOrder.SlotReuse);
    var bools = IrType.Set(IrType.Bool, MapOrder.SlotReuse);
    var o = new IrLocal("o", objects);
    var s = new IrLocal("s", strings);
    var l = new IrLocal("l", longs);
    var b = new IrLocal("b", bools);
    IrExpr Add(IrLocal set, IrExpr value) => Op(IrCollectionOp.SetAdd, IrType.Bool, R(set), value);
    IrExpr Count(IrLocal set) => Op(IrCollectionOp.SetCount, IrType.I32, R(set));
    var module = new IrModule("M", [F(IrType.I32, [o, s, l, b],
      new IrDeclare(o, Op(IrCollectionOp.SetNew, objects)),
      new IrDeclare(s, Op(IrCollectionOp.SetNew, strings)),
      new IrDeclare(l, Op(IrCollectionOp.SetNew, longs)),
      new IrDeclare(b, Op(IrCollectionOp.SetNew, bools)),
      new IrExprStmt(Add(o, new IrNew(IrType.Object(type), constructor, []))),
      new IrExprStmt(Add(o, new IrNew(IrType.Object(type), constructor, []))),
      new IrExprStmt(Add(s, new IrStrConst("a"))),
      new IrIf(Add(s, new IrStrConst("a")), new IrBlock([new IrReturn(I(-1))]), null),
      new IrExprStmt(Add(l, new IrConst(IrType.I64, 1L << 40))),
      new IrExprStmt(Add(l, new IrConst(IrType.I64, 1L << 40))),
      new IrExprStmt(Add(b, new IrConst(IrType.Bool, 1))),
      new IrExprStmt(Add(b, new IrConst(IrType.Bool, 0))),
      new IrReturn(new IrBinary(IrBinaryOp.Add, new IrBinary(IrBinaryOp.Multiply, Count(o), I(1000)),
        new IrBinary(IrBinaryOp.Add, new IrBinary(IrBinaryOp.Multiply, Count(s), I(100)),
          new IrBinary(IrBinaryOp.Add, new IrBinary(IrBinaryOp.Multiply, Count(l), I(10)), Count(b))))))
      , body], []) { Types = [type] };
    Assert.That(Problems(module), Is.Empty);
    Assert.That(IrInterpreter.Call(module, "F", []), Is.EqualTo(2112));
  }

  /// <summary>Test design 10: a missing key traps, a present key added again traps, try-add and try-get do not.</summary>
  [Test]
  public void MapTraps_AndTheirTryForms() {
    var type = IrType.Map(IrType.String, IrType.I32, MapOrder.SlotReuse);
    var map = new IrLocal("m", type);
    IrStmt[] Start() => [new IrDeclare(map, Op(IrCollectionOp.MapNew, type)), Do(IrCollectionOp.MapAdd, R(map), new IrStrConst("a"), I(1))];
    Assert.That(Trap(IrType.I32, [map], [.. Start(), new IrReturn(Op(IrCollectionOp.MapGet, IrType.I32, R(map), new IrStrConst("b")))]), Is.EqualTo(IrTrap.KeyNotFound));
    Assert.That(Trap(IrType.I32, [map], [.. Start(), Do(IrCollectionOp.MapAdd, R(map), new IrStrConst("a"), I(2)), new IrReturn(I(0))]), Is.EqualTo(IrTrap.DuplicateKey));
    var result = Run(IrType.I32, [map], [.. Start(),
      new IrIf(Op(IrCollectionOp.MapTryAdd, IrType.Bool, R(map), new IrStrConst("a"), I(5)), new IrBlock([new IrReturn(I(-1))]), null),
      new IrIf(new IrUnary(IrUnaryOp.LogicalNot, Op(IrCollectionOp.MapContainsKey, IrType.Bool, R(map), new IrStrConst("a"))), new IrBlock([new IrReturn(I(-2))]), null),
      new IrReturn(new IrBinary(IrBinaryOp.Add,
        new IrCoalesce(Op(IrCollectionOp.MapTryGet, IrType.Optional(IrType.I32), R(map), new IrStrConst("a")), I(100)),
        new IrCoalesce(Op(IrCollectionOp.MapTryGet, IrType.Optional(IrType.I32), R(map), new IrStrConst("z")), I(10))))]);
    Assert.That(result, Is.EqualTo(11));
    Assert.That(IrExceptionClass.Of(IrTrap.KeyNotFound), Is.EqualTo(IrExceptionClass.KeyNotFoundException));
    Assert.That(IrExceptionClass.Of(IrTrap.DuplicateKey), Is.EqualTo(IrExceptionClass.ArgumentException));
    Assert.That(IrExceptionClass.Of(IrTrap.CollectionModified), Is.EqualTo(IrExceptionClass.InvalidOperationException));
  }

  [Test]
  public void TryGet_OfObjectValues_IsNull() {
    var type = new IrClassType("C");
    type.Define([]);
    type.DefineMembers([], [], [], []);
    var map = IrType.Map(IrType.I32, IrType.Object(type), MapOrder.Insertion);
    var m = new IrLocal("m", map);
    var module = new IrModule("M", [F(IrType.Bool, [m],
      new IrDeclare(m, Op(IrCollectionOp.MapNew, map)),
      new IrReturn(new IrIsNull(Op(IrCollectionOp.MapTryGet, IrType.Object(type), R(m), I(1)))))], []) { Types = [type] };
    Assert.That(Problems(module), Is.Empty);
    Assert.That(IrInterpreter.Call(module, "F", []), Is.True);
  }

  /// <summary>Test design 4: a checked loop over a list traps at the step after a change, also after the last pass.</summary>
  [TestCase(IrCollectionOp.ListSet, 0, 1)]
  [TestCase(IrCollectionOp.ListAdd, 2, 3)]
  [TestCase(IrCollectionOp.ListClear, 1, 2)]
  [TestCase(IrCollectionOp.ListRemoveAt, 0, 1)]
  [TestCase(IrCollectionOp.ListInsert, 1, 2)]
  public void CheckedList_TrapsAfterAChange(IrCollectionOp op, int at, int passes) {
    var list = new IrLocal("l", IntList);
    var item = new IrLocal("x", IrType.I32);
    var count = new IrLocal("n", IrType.I32);
    var change = op switch {
      IrCollectionOp.ListAdd => Do(op, R(list), I(9)),
      IrCollectionOp.ListClear => Do(op, R(list)),
      IrCollectionOp.ListRemoveAt => Do(op, R(list), I(0)),
      _ => Do(op, R(list), I(0), I(9))
    };
    var module = Module(new IrFunction("F", true, IrType.I32, [], [list, item, count], new IrBlock([
      new IrDeclare(list, Op(IrCollectionOp.ListFromArray, IntList, new IrNewArray(IrType.Array(IrType.I32), I(3)))),
      new IrDeclare(count, I(0)),
      new IrTry(new IrBlock([
        new IrForEachCollection(R(list), EnumerationMode.Checked, item, null, new IrBlock([
          new IrAssign(count, new IrBinary(IrBinaryOp.Add, R(count), I(1))),
          new IrIf(new IrBinary(IrBinaryOp.Equal, R(count), I(at + 1)), new IrBlock([change]), null)]))]),
        [new IrCatch(IrExceptionClass.InvalidOperationException, null, null, new IrBlock([new IrReturn(R(count))]))], null),
      new IrReturn(I(-1))])));
    Assert.That(Problems(module), Is.Empty);
    Assert.That(IrInterpreter.Call(module, "F", []), Is.EqualTo(passes));
  }

  /// <summary>A change that traps itself changes nothing a checked loop notices.</summary>
  [Test]
  public void CheckedList_IgnoresAChangeThatTrapped() {
    var list = new IrLocal("l", IntList);
    var item = new IrLocal("x", IrType.I32);
    var count = new IrLocal("n", IrType.I32);
    var result = Run(IrType.I32, [list, item, count],
      new IrDeclare(list, Op(IrCollectionOp.ListFromArray, IntList, new IrNewArray(IrType.Array(IrType.I32), I(2)))),
      new IrDeclare(count, I(0)),
      new IrForEachCollection(R(list), EnumerationMode.Checked, item, null, new IrBlock([
        new IrAssign(count, new IrBinary(IrBinaryOp.Add, R(count), I(1))),
        new IrTry(new IrBlock([Do(IrCollectionOp.ListRemoveAt, R(list), I(5))]),
          [new IrCatch(IrExceptionClass.ArgumentOutOfRangeException, null, null, new IrBlock([]))], null)])),
      new IrReturn(R(count)));
    Assert.That(result, Is.EqualTo(2));
  }

  /// <summary>Test design 4: JavaScript walks a list to its current length; pushing in every pass of [1, 2] while short of four elements makes four passes.</summary>
  [Test]
  public void LiveList_SeesWhatIsAdded() {
    var list = new IrLocal("l", IntList);
    var item = new IrLocal("x", IrType.I32);
    var count = new IrLocal("n", IrType.I32);
    var result = Run(IrType.I32, [list, item, count],
      new IrDeclare(list, Op(IrCollectionOp.ListFromArray, IntList, new IrNewArray(IrType.Array(IrType.I32), I(2)))),
      new IrDeclare(count, I(0)),
      new IrForEachCollection(R(list), EnumerationMode.Live, item, null, new IrBlock([
        new IrAssign(count, new IrBinary(IrBinaryOp.Add, R(count), I(1))),
        new IrIf(new IrBinary(IrBinaryOp.Less, Op(IrCollectionOp.ListCount, IrType.I32, R(list)), I(4)), new IrBlock([Do(IrCollectionOp.ListAdd, R(list), I(9))]), null)])),
      new IrReturn(R(count)));
    Assert.That(result, Is.EqualTo(4));
  }

  /// <summary>Test design 4: a checked map traps after a new key, not after an overwrite, a removal or a clear; a removal still skips the entry.</summary>
  [TestCase("new", -1)]
  [TestCase("overwrite", 3)]
  [TestCase("remove", 2)]
  [TestCase("clear", 1)]
  public void CheckedMap_TrapsOnlyAfterANewKey(string change, int passes) {
    var type = IrType.Map(IrType.I32, IrType.I32, MapOrder.SlotReuse);
    var map = new IrLocal("m", type);
    var key = new IrLocal("k", IrType.I32);
    var count = new IrLocal("n", IrType.I32);
    IrStmt act = change switch {
      "new" => Do(IrCollectionOp.MapPut, R(map), I(99), I(0)),
      "overwrite" => Do(IrCollectionOp.MapPut, R(map), I(2), I(7)),
      "remove" => new IrExprStmt(Op(IrCollectionOp.MapRemove, IrType.Bool, R(map), I(2))),
      _ => Do(IrCollectionOp.MapClear, R(map))
    };
    var module = Module(F(IrType.I32, [map, key, count], [
      new IrDeclare(map, Op(IrCollectionOp.MapNew, type)),
      new IrDeclare(count, I(0)),
      .. Insert(map, 1, 2, 3),
      new IrTry(new IrBlock([
        new IrForEachCollection(R(map), EnumerationMode.Checked, key, null, new IrBlock([
          new IrAssign(count, new IrBinary(IrBinaryOp.Add, R(count), I(1))),
          new IrIf(new IrBinary(IrBinaryOp.Equal, R(key), I(1)), new IrBlock([act]), null)]))]),
        [new IrCatch(IrExceptionClass.InvalidOperationException, null, null, new IrBlock([new IrReturn(new IrUnary(IrUnaryOp.Negate, R(count)))]))], null),
      new IrReturn(R(count))]));
    Assert.That(Problems(module), Is.Empty);
    Assert.That(IrInterpreter.Call(module, "F", []), Is.EqualTo(passes));
  }

  /// <summary>Test design 4: a live map visits the keys added during the walk and skips the ones removed before it reaches them.</summary>
  [Test]
  public void LiveMap_VisitsAddedAndSkipsRemoved() {
    var type = IrType.Map(IrType.I32, IrType.I32, MapOrder.Insertion);
    var map = new IrLocal("m", type);
    var key = new IrLocal("k", IrType.I32);
    var sum = new IrLocal("s", IrType.I32);
    var result = Run(IrType.I32, [map, key, sum], [
      new IrDeclare(map, Op(IrCollectionOp.MapNew, type)),
      new IrDeclare(sum, I(0)),
      .. Insert(map, 1, 2, 3),
      new IrForEachCollection(R(map), EnumerationMode.Live, key, null, new IrBlock([
        Times10Plus(sum, R(key)),
        new IrIf(new IrBinary(IrBinaryOp.Equal, R(key), I(1)), new IrBlock([.. Remove(map, 2), .. Insert(map, 4)]), null),
        new IrIf(new IrBinary(IrBinaryOp.Equal, R(key), I(4)), new IrBlock([Do(IrCollectionOp.MapClear, R(map)), .. Insert(map, 5)]), null)])),
      new IrReturn(R(sum))]);
    Assert.That(result, Is.EqualTo(1345));
  }

  /// <summary>Values of a map alone, and break and continue in a loop over a collection.</summary>
  [Test]
  public void Loop_OverValues_BreaksAndContinues() {
    var type = IrType.Map(IrType.I32, IrType.I32, MapOrder.Insertion);
    var map = new IrLocal("m", type);
    var value = new IrLocal("v", IrType.I32);
    var sum = new IrLocal("s", IrType.I32);
    var result = Run(IrType.I32, [map, value, sum], [
      new IrDeclare(map, Op(IrCollectionOp.MapNew, type)),
      new IrDeclare(sum, I(0)),
      .. Insert(map, 1, 2, 3, 4),
      new IrForEachCollection(R(map), EnumerationMode.Checked, null, value, new IrBlock([
        new IrIf(new IrBinary(IrBinaryOp.Equal, R(value), I(2)), new IrBlock([new IrContinue()]), null),
        new IrIf(new IrBinary(IrBinaryOp.Equal, R(value), I(4)), new IrBlock([new IrBreak()]), null),
        Times10Plus(sum, R(value))])),
      new IrReturn(R(sum))]);
    Assert.That(result, Is.EqualTo(13));
  }

  [Test]
  public void Loop_ReturnsFromInside() {
    var list = new IrLocal("l", IntList);
    var item = new IrLocal("x", IrType.I32);
    var result = Run(IrType.I32, [list, item],
      new IrDeclare(list, Op(IrCollectionOp.ListNew, IntList)),
      Do(IrCollectionOp.ListAdd, R(list), I(8)),
      new IrForEachCollection(R(list), EnumerationMode.Live, item, null, new IrBlock([new IrReturn(R(item))])),
      new IrReturn(I(0)));
    Assert.That(result, Is.EqualTo(8));
  }

  /// <summary>Test design 12: the verifier refuses what no collection does.</summary>
  [Test]
  public void Verifier_RefusesIllTypedCollections() {
    var list = new IrLocal("l", IntList);
    var number = new IrLocal("n", IrType.I32);
    var key = new IrLocal("k", IrType.F64);
    var map = IrType.Map(IrType.I32, IrType.I32, MapOrder.Insertion);
    var problems = Problems(Module(F(IrType.Void, [list, number, key],
      new IrDeclare(list, Op(IrCollectionOp.ListNew, IntList)),
      new IrDeclare(number, I(0)),
      Do(IrCollectionOp.ListAdd, R(list), D(1.5)),
      Do(IrCollectionOp.ListAdd, R(list)),
      new IrExprStmt(Op(IrCollectionOp.ListCount, IrType.I64, R(list))),
      new IrExprStmt(Op(IrCollectionOp.MapCount, IrType.I32, R(list))),
      new IrExprStmt(Op(IrCollectionOp.ListCount, IrType.I32)),
      new IrExprStmt(Op(IrCollectionOp.ListRemoveLast, IrType.String, Op(IrCollectionOp.ListNew, IrType.List(IrType.String)))),
      new IrExprStmt(Op(IrCollectionOp.SetNew, IrType.Set(IrType.Span(IrType.U8), MapOrder.Insertion))),
      new IrForEachCollection(R(number), EnumerationMode.Live, number, null, new IrBlock([])),
      new IrForEachCollection(R(list), EnumerationMode.Live, key, null, new IrBlock([])),
      new IrForEachCollection(R(list), EnumerationMode.Live, null, null, new IrBlock([])),
      new IrForEachCollection(R(list), EnumerationMode.Live, number, number, new IrBlock([])),
      new IrForEachCollection(Op(IrCollectionOp.MapNew, map), EnumerationMode.Live, number, key, new IrBlock([])),
      new IrForEachCollection(R(list), EnumerationMode.Live, new IrLocal("ghost", IrType.I32), null, new IrBlock([])))));
    Assert.That(problems, Has.Some.Contains("operand 1 of ListAdd has the type f64, expected i32"));
    Assert.That(problems, Has.Some.Contains("ListAdd takes 1 operands, not 0"));
    Assert.That(problems, Has.Some.Contains("ListCount has the type i64, expected i32"));
    Assert.That(problems, Has.Some.Contains("MapCount is applied to a value of the type list<i32>, which is no map"));
    Assert.That(problems, Has.Some.Contains("ListCount is applied to a value of the type nothing"));
    Assert.That(problems, Has.Some.Contains("ListRemoveLast of a list<str>, whose elements have no absent value"));
    Assert.That(problems, Has.Some.Contains("holds elements no collection holds"));
    Assert.That(problems, Has.Some.Contains("a loop over a value of the type i32, which is no collection"));
    Assert.That(problems, Has.Some.Contains("the element 'k' of a loop over a list<i32> has the type f64"));
    Assert.That(problems, Has.Some.Contains("a loop over a collection without an element"));
    Assert.That(problems, Has.Some.Contains("a loop over a list<i32> with a value"));
    Assert.That(problems, Has.Some.Contains("the value 'k' of a loop over a map<i32, i32, insertion> has the type f64"));
    Assert.That(problems, Has.Some.Contains("local 'n' is declared twice"));
    Assert.That(problems, Has.Some.Contains("local 'ghost' is not among the locals of the function"));
  }

  [Test]
  public void Verifier_RefusesAnUndeclaredElementType() {
    var point = new IrStructType("P");
    point.Define([new IrField("X", IrType.I32, false)]);
    var type = IrType.Map(IrType.I32, IrType.Struct(point), MapOrder.Insertion);
    var problems = Problems(Module(F(IrType.Void, [], new IrExprStmt(Op(IrCollectionOp.MapNew, type)))));
    Assert.That(problems, Has.Some.Contains("the type P is not among the types of the module"));
  }

  [Test]
  public void Loop_ElementIsOutOfScopeAfterIt() {
    var list = new IrLocal("l", IntList);
    var item = new IrLocal("x", IrType.I32);
    var problems = Problems(Module(F(IrType.I32, [list, item],
      new IrDeclare(list, Op(IrCollectionOp.ListNew, IntList)),
      new IrForEachCollection(R(list), EnumerationMode.Live, item, null, new IrBlock([new IrContinue()])),
      new IrReturn(R(item)))));
    Assert.That(problems, Has.Some.Contains("'x' is used outside the scope that declares it"));
  }

  [Test]
  public void TreeWalkers_SeeTheOperandsAndTheBody() {
    var list = new IrLocal("l", IntList);
    var item = new IrLocal("x", IrType.I32);
    var add = Op(IrCollectionOp.ListAdd, IrType.Void, R(list), I(1));
    var loop = new IrForEachCollection(R(list), EnumerationMode.Live, item, null, new IrBlock([new IrExprStmt(add)]));
    Assert.That(IrTree.Operands(add), Is.EqualTo(new IrExpr[] { R(list), I(1) }));
    Assert.That(IrTree.Expressions(loop), Is.EqualTo(new IrExpr[] { R(list) }));
    Assert.That(IrTree.Blocks(loop), Is.EqualTo(new[] { loop.Body }));
    Assert.That(IrTree.Nodes(loop).OfType<IrCollection>().Single(), Is.SameAs(add));
  }

}
