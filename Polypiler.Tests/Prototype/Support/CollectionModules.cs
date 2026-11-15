using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// Modules of lists built by hand, for the operations no C# source writes - JavaScript's <c>pop</c>,
/// <c>indexOf</c> and <c>includes</c> and the live loop - and for the element types the fixtures do not
/// reach. Every function is public, takes nothing and returns an <c>i32</c>, so the interpreter, the
/// generated C# and the generated JavaScript can be compared call by call.
/// </summary>
internal static class CollectionModules {

  private static IrConst I(long value) => new(IrType.I32, value);

  private static IrConst D(double value) => new(IrType.F64, IrOperators.Bits(IrType.F64, value));

  private static IrLocalRef R(IrLocal local) => new(local);

  private static IrCollection Op(IrCollectionOp op, IrType type, params IrExpr[] arguments) => new(op, type, arguments);

  private static IrExprStmt Do(IrCollectionOp op, params IrExpr[] arguments) => new(Op(op, IrType.Void, arguments));

  private static IrBinary Plus(IrExpr left, IrExpr right) => new(IrBinaryOp.Add, left, right);

  private static IrBinary Times(IrExpr left, long factor) => new(IrBinaryOp.Multiply, left, I(factor));

  private static IrExpr Count(IrLocal list) => Op(IrCollectionOp.ListCount, IrType.I32, R(list));

  private static IrExpr Bit(IrExpr truth) => new IrConditional(truth, I(1), I(0));

  /// <summary>The names of the functions of <see cref="Lists"/>.</summary>
  public static readonly string[] ListFunctions = [
    "PopValues", "PopObjects", "StrictSearch", "StrictSingles", "Includes", "LivePush", "LiveTemporary", "CheckedThroughCall",
    "CheckedLast", "CopyStructs", "CopyStrings", "CopyLongs", "SearchStructs", "SearchRecords", "ChangesCounted"
  ];

  /// <summary>
  /// A module of lists: pop of values and of objects, JavaScript's searches, live loops that push, a
  /// checked loop whose body changes the list through a call, copies of structs, strings and 64-bit
  /// integers, and searches by <c>Equals</c> of structs and record classes.
  /// </summary>
  public static IrModule Lists() {
    var functions = new List<IrFunction>();
    IrFunction F(string name, IReadOnlyList<IrLocal> locals, params IrStmt[] statements)
      => new(name, true, IrType.I32, [], [.. locals], new IrBlock(statements));

    var ints = IrType.List(IrType.I32);
    var doubles = IrType.List(IrType.F64);
    var singles = IrType.List(IrType.F32);
    var optional = IrType.Optional(IrType.I32);

    // pop of values: [4, 9] pops 9, 4, then none, which is 100.
    var list = new IrLocal("list", ints);
    functions.Add(F("PopValues", [list],
      new IrDeclare(list, Op(IrCollectionOp.ListNew, ints)),
      Do(IrCollectionOp.ListAdd, R(list), I(4)),
      Do(IrCollectionOp.ListAdd, R(list), I(9)),
      new IrReturn(Plus(Times(new IrCoalesce(Op(IrCollectionOp.ListRemoveLast, optional, R(list)), I(0)), 1000),
        Plus(Times(new IrCoalesce(Op(IrCollectionOp.ListRemoveLast, optional, R(list)), I(0)), 100),
          Plus(new IrCoalesce(Op(IrCollectionOp.ListRemoveLast, optional, R(list)), I(100)), Times(Count(list), 7)))))));

    // pop of objects: the box itself, then null.
    var box = new IrClassType("Box");
    var value = new IrField("Value", IrType.I32, false);
    box.Define([value]);
    var make = new IrConstructor("Box..ctor");
    box.DefineMembers([make], [], [], []);
    var self = new IrLocal("this", IrType.Object(box));
    var start = new IrLocal("start", IrType.I32);
    functions.Add(new IrFunction("Box..ctor", false, IrType.Void, [self, start], [], new IrBlock([new IrFieldStore(R(self), value, R(start))])));
    var boxes = IrType.List(IrType.Object(box));
    var bag = new IrLocal("bag", boxes);
    var popped = new IrLocal("popped", IrType.Object(box));
    functions.Add(F("PopObjects", [bag, popped],
      new IrDeclare(bag, Op(IrCollectionOp.ListNew, boxes)),
      Do(IrCollectionOp.ListAdd, R(bag), new IrNew(IrType.Object(box), make, [I(6)])),
      new IrDeclare(popped, Op(IrCollectionOp.ListRemoveLast, IrType.Object(box), R(bag))),
      new IrReturn(Plus(Times(new IrFieldLoad(R(popped), value), 10), Bit(new IrIsNull(Op(IrCollectionOp.ListRemoveLast, IrType.Object(box), R(bag))))))));

    // JavaScript's indexOf on [NaN, -0, 2]: no NaN, -0 found as 0, 2 at 2.
    var numbers = new IrLocal("numbers", doubles);
    functions.Add(F("StrictSearch", [numbers],
      new IrDeclare(numbers, Op(IrCollectionOp.ListNew, doubles)),
      Do(IrCollectionOp.ListAdd, R(numbers), D(double.NaN)),
      Do(IrCollectionOp.ListAdd, R(numbers), D(-0.0)),
      Do(IrCollectionOp.ListAdd, R(numbers), D(2)),
      new IrReturn(Plus(Times(Op(IrCollectionOp.ListIndexOfStrict, IrType.I32, R(numbers), D(double.NaN)), 100),
        Plus(Times(Op(IrCollectionOp.ListIndexOfStrict, IrType.I32, R(numbers), D(0)), 10), Op(IrCollectionOp.ListIndexOfStrict, IrType.I32, R(numbers), D(2)))))));

    var floats = new IrLocal("floats", singles);
    var half = new IrConst(IrType.F32, IrOperators.Bits(IrType.F32, 0.5));
    functions.Add(F("StrictSingles", [floats],
      new IrDeclare(floats, Op(IrCollectionOp.ListNew, singles)),
      Do(IrCollectionOp.ListAdd, R(floats), new IrConst(IrType.F32, IrOperators.Bits(IrType.F32, 1.5))),
      Do(IrCollectionOp.ListAdd, R(floats), half),
      new IrReturn(Op(IrCollectionOp.ListIndexOfStrict, IrType.I32, R(floats), half))));

    // includes on [NaN, -0]: NaN and 0 found, 1 not; indexOf and Contains by Equals of the integers.
    var found = new IrLocal("found", doubles);
    var small = new IrLocal("small", ints);
    functions.Add(F("Includes", [found, small],
      new IrDeclare(found, Op(IrCollectionOp.ListNew, doubles)),
      Do(IrCollectionOp.ListAdd, R(found), D(double.NaN)),
      Do(IrCollectionOp.ListAdd, R(found), D(-0.0)),
      new IrDeclare(small, Op(IrCollectionOp.ListNew, ints)),
      Do(IrCollectionOp.ListAdd, R(small), I(3)),
      new IrReturn(Plus(Times(Bit(Op(IrCollectionOp.ListIncludesSameValueZero, IrType.Bool, R(found), D(double.NaN))), 1000),
        Plus(Times(Bit(Op(IrCollectionOp.ListIncludesSameValueZero, IrType.Bool, R(found), D(0))), 100),
          Plus(Times(Bit(Op(IrCollectionOp.ListIncludesSameValueZero, IrType.Bool, R(found), D(1))), 10),
            Plus(Bit(Op(IrCollectionOp.ListContains, IrType.Bool, R(small), I(3))), Times(Op(IrCollectionOp.ListIndexOfEquals, IrType.I32, R(small), I(3)), 5))))))));

    // A live loop over [1, 2] that pushes while the list is shorter than four makes four passes.
    var grown = new IrLocal("grown", ints);
    var item = new IrLocal("item", IrType.I32);
    var passes = new IrLocal("passes", IrType.I32);
    functions.Add(F("LivePush", [grown, item, passes],
      new IrDeclare(grown, Op(IrCollectionOp.ListNew, ints)),
      Do(IrCollectionOp.ListAdd, R(grown), I(1)),
      Do(IrCollectionOp.ListAdd, R(grown), I(2)),
      new IrDeclare(passes, I(0)),
      new IrForEachCollection(R(grown), EnumerationMode.Live, item, null, new IrBlock([
        new IrAssign(passes, Plus(R(passes), I(1))),
        new IrIf(new IrBinary(IrBinaryOp.Less, Count(grown), I(4)), new IrBlock([Do(IrCollectionOp.ListAdd, R(grown), R(item))]), null)])),
      new IrReturn(Plus(Times(R(passes), 10), Count(grown)))));

    // A live loop over a list that is no variable: the list is evaluated once.
    var fresh = new IrLocal("fresh", IrType.I32);
    var total = new IrLocal("total", IrType.I32);
    var source = new IrLocal("source", IrType.Array(IrType.I32));
    functions.Add(F("LiveTemporary", [source, fresh, total],
      new IrDeclare(source, new IrNewArray(IrType.Array(IrType.I32), I(3))),
      new IrStore(R(source), I(1), I(5)),
      new IrDeclare(total, I(0)),
      new IrForEachCollection(Op(IrCollectionOp.ListFromArray, ints, R(source)), EnumerationMode.Live, fresh, null, new IrBlock([
        new IrAssign(total, Plus(Times(R(total), 10), R(fresh)))])),
      new IrReturn(R(total))));

    // A checked loop whose body adds through a call traps after the first pass.
    var target = new IrLocal("target", ints);
    var added = new IrLocal("added", ints);
    functions.Add(new IrFunction("Append", false, IrType.Void, [added], [], new IrBlock([Do(IrCollectionOp.ListAdd, R(added), I(1))])));
    var counted = new IrLocal("counted", IrType.I32);
    var each = new IrLocal("each", IrType.I32);
    functions.Add(F("CheckedThroughCall", [target, counted, each],
      new IrDeclare(target, Op(IrCollectionOp.ListNew, ints)),
      Do(IrCollectionOp.ListAdd, R(target), I(1)),
      Do(IrCollectionOp.ListAdd, R(target), I(2)),
      new IrDeclare(counted, I(0)),
      new IrTry(new IrBlock([
        new IrForEachCollection(R(target), EnumerationMode.Checked, each, null, new IrBlock([
          new IrAssign(counted, Plus(R(counted), I(1))),
          new IrExprStmt(new IrCall("Append", IrType.Void, [R(target)]))]))]),
        [new IrCatch(IrExceptionClass.InvalidOperationException, null, null, new IrBlock([new IrReturn(new IrUnary(IrUnaryOp.Negate, R(counted)))]))], null),
      new IrReturn(R(counted))));

    // A checked loop whose last pass sets an element, clears, inserts, removes and pops: each counts.
    var changed = new IrLocal("changed", ints);
    var step = new IrLocal("step", IrType.I32);
    var seen = new IrLocal("seen", IrType.I32);
    functions.Add(F("CheckedLast", [changed, step, seen],
      new IrDeclare(changed, Op(IrCollectionOp.ListNew, ints)),
      Do(IrCollectionOp.ListAdd, R(changed), I(1)),
      new IrDeclare(seen, I(0)),
      new IrTry(new IrBlock([
        new IrForEachCollection(R(changed), EnumerationMode.Checked, step, null, new IrBlock([
          new IrAssign(seen, Plus(R(seen), I(1))),
          Do(IrCollectionOp.ListSet, R(changed), I(0), I(2))]))]),
        [new IrCatch(IrExceptionClass.InvalidOperationException, null, null, new IrBlock([new IrAssign(seen, Plus(R(seen), I(10)))]))], null),
      Do(IrCollectionOp.ListInsert, R(changed), I(0), I(3)),
      Do(IrCollectionOp.ListRemoveAt, R(changed), I(1)),
      new IrExprStmt(Op(IrCollectionOp.ListRemoveLast, optional, R(changed))),
      Do(IrCollectionOp.ListClear, R(changed)),
      new IrReturn(Plus(Times(R(seen), 10), Count(changed)))));

    // Structs are copied into an array and out of one.
    var point = new IrStructType("Point");
    var x = new IrField("X", IrType.I32, false);
    point.Define([x]);
    var points = IrType.List(IrType.Struct(point));
    var path = new IrLocal("path", points);
    var copy = new IrLocal("copy", IrType.Array(IrType.Struct(point)));
    var back = new IrLocal("back", points);
    functions.Add(F("CopyStructs", [path, copy, back],
      new IrDeclare(path, Op(IrCollectionOp.ListNew, points)),
      Do(IrCollectionOp.ListAdd, R(path), new IrStructNew(IrType.Struct(point), [I(1)])),
      new IrDeclare(copy, Op(IrCollectionOp.ListCopyToArray, IrType.Array(IrType.Struct(point)), R(path))),
      new IrFieldStore(new IrArrayLoad(R(copy), I(0)), x, I(5)),
      new IrDeclare(back, Op(IrCollectionOp.ListFromArray, points, R(copy))),
      new IrFieldStore(new IrArrayLoad(R(copy), I(0)), x, I(7)),
      new IrReturn(Plus(Times(new IrFieldLoad(Op(IrCollectionOp.ListGet, IrType.Struct(point), R(path), I(0)), x), 10),
        new IrFieldLoad(Op(IrCollectionOp.ListGet, IrType.Struct(point), R(back), I(0)), x)))));

    var strings = IrType.List(IrType.String);
    var words = new IrLocal("words", strings);
    var array = new IrLocal("array", IrType.Array(IrType.String));
    functions.Add(F("CopyStrings", [words, array],
      new IrDeclare(words, Op(IrCollectionOp.ListNew, strings)),
      Do(IrCollectionOp.ListAdd, R(words), new IrStrConst("ab")),
      new IrDeclare(array, Op(IrCollectionOp.ListCopyToArray, IrType.Array(IrType.String), R(words))),
      Do(IrCollectionOp.ListClear, R(words)),
      new IrReturn(Plus(Times(new IrArrayLength(R(array)), 10), Count(words)))));

    var longs = IrType.List(IrType.I64);
    var wide = new IrLocal("wide", longs);
    var wides = new IrLocal("wides", IrType.Array(IrType.I64));
    functions.Add(F("CopyLongs", [wide, wides],
      new IrDeclare(wide, Op(IrCollectionOp.ListNew, longs)),
      Do(IrCollectionOp.ListAdd, R(wide), new IrConst(IrType.I64, 1L << 40)),
      new IrDeclare(wides, Op(IrCollectionOp.ListCopyToArray, IrType.Array(IrType.I64), R(wide))),
      new IrReturn(new IrConvert(IrType.I32, new IrBinary(IrBinaryOp.ShiftRightLogical, new IrArrayLoad(R(wides), I(0)), new IrConst(IrType.I32, 38)), IrConversion.Wrap))));

    // IndexOf and Contains by Equals of structs and of record classes.
    var pointList = new IrLocal("pointList", points);
    functions.Add(F("SearchStructs", [pointList],
      new IrDeclare(pointList, Op(IrCollectionOp.ListNew, points)),
      Do(IrCollectionOp.ListAdd, R(pointList), new IrStructNew(IrType.Struct(point), [I(1)])),
      Do(IrCollectionOp.ListAdd, R(pointList), new IrStructNew(IrType.Struct(point), [I(2)])),
      new IrReturn(Plus(Times(Op(IrCollectionOp.ListIndexOfEquals, IrType.I32, R(pointList), new IrStructNew(IrType.Struct(point), [I(2)])), 10),
        Bit(Op(IrCollectionOp.ListContains, IrType.Bool, R(pointList), new IrStructNew(IrType.Struct(point), [I(3)])))))));

    var tag = new IrClassType("Tag") { IsRecord = true };
    var name = new IrField("Name", IrType.I32, true);
    tag.Define([name]);
    var makeTag = new IrConstructor("Tag..ctor");
    tag.DefineMembers([makeTag], [], [], []);
    var tagSelf = new IrLocal("this", IrType.Object(tag));
    var tagStart = new IrLocal("name", IrType.I32);
    functions.Add(new IrFunction("Tag..ctor", false, IrType.Void, [tagSelf, tagStart], [], new IrBlock([new IrFieldStore(R(tagSelf), name, R(tagStart))])));
    var tags = IrType.List(IrType.Object(tag));
    var tagList = new IrLocal("tagList", tags);
    functions.Add(F("SearchRecords", [tagList],
      new IrDeclare(tagList, Op(IrCollectionOp.ListNew, tags)),
      Do(IrCollectionOp.ListAdd, R(tagList), new IrNew(IrType.Object(tag), makeTag, [I(4)])),
      new IrReturn(Plus(Times(Op(IrCollectionOp.ListIndexOfEquals, IrType.I32, R(tagList), new IrNew(IrType.Object(tag), makeTag, [I(4)])), 10),
        Bit(Op(IrCollectionOp.ListIncludesSameValueZero, IrType.Bool, R(tagList), new IrNew(IrType.Object(tag), makeTag, [I(4)])))))));

    // The counted changes of a module with a checked loop that changes its list: insertion, removal and pop outside it.
    var counting = new IrLocal("counting", ints);
    var unit = new IrLocal("unit", IrType.I32);
    functions.Add(F("ChangesCounted", [counting, unit],
      new IrDeclare(counting, Op(IrCollectionOp.ListNew, ints)),
      Do(IrCollectionOp.ListAdd, R(counting), I(1)),
      Do(IrCollectionOp.ListInsert, R(counting), I(0), I(2)),
      Do(IrCollectionOp.ListRemoveAt, R(counting), I(1)),
      new IrForEachCollection(R(counting), EnumerationMode.Checked, unit, null, new IrBlock([new IrBreak()])),
      new IrReturn(Plus(Times(new IrCoalesce(Op(IrCollectionOp.ListRemoveLast, optional, R(counting)), I(0)), 10),
        new IrCoalesce(Op(IrCollectionOp.ListRemoveLast, optional, R(counting)), I(9))))));

    return new IrModule("Lists", functions) { Types = [box, point, tag] };
  }

  /// <summary>The calls of <see cref="Lists"/>.</summary>
  public static IEnumerable<PrototypeCall> ListCalls() => ListFunctions.Select(function => new PrototypeCall(function, []) { Returns = typeof(int) });

  /// <summary>The names of the functions of <see cref="Maps"/>.</summary>
  public static readonly string[] MapFunctions = [
    "InsertionRemoved", "InsertionLive", "InsertionSet", "Lookups", "ObjectKeys", "LongKeys", "SingleKeys", "StructValues", "ValuesOnly", "SlotsLive"
  ];

  /// <summary>
  /// A module of maps and sets: JavaScript's order with removals and a live loop that inserts, sets of
  /// both orders, the trapping and the trying lookups of values and references, object, 64-bit and
  /// binary32 keys, struct values, loops over the values alone, and a live loop over .NET's order.
  /// </summary>
  public static IrModule Maps() {
    var functions = new List<IrFunction>();
    IrFunction F(string name, IReadOnlyList<IrLocal> locals, params IrStmt[] statements)
      => new(name, true, IrType.I32, [], [.. locals], new IrBlock(statements));

    var insertion = IrType.Map(IrType.I32, IrType.I32, MapOrder.Insertion);
    var slots = IrType.Map(IrType.I32, IrType.I32, MapOrder.SlotReuse);
    IrStmt Put(IrLocal map, long key, long value) => Do(IrCollectionOp.MapPut, R(map), I(key), I(value));
    IrStmt Remove(IrLocal map, long key) => new IrExprStmt(Op(IrCollectionOp.MapRemove, IrType.Bool, R(map), I(key)));
    IrStmt Digit(IrLocal sum, IrExpr value) => new IrAssign(sum, Plus(Times(R(sum), 10), value));

    // JavaScript's order with removals: 1, 2, 3, remove 1 and 3, then 4, 5, 6 is 2456.
    var m = new IrLocal("m", insertion);
    var k = new IrLocal("k", IrType.I32);
    var sum = new IrLocal("sum", IrType.I32);
    functions.Add(F("InsertionRemoved", [m, k, sum],
      new IrDeclare(m, Op(IrCollectionOp.MapNew, insertion)),
      new IrDeclare(sum, I(0)),
      Put(m, 1, 1), Put(m, 2, 2), Put(m, 3, 3), Remove(m, 1), Remove(m, 3), Put(m, 4, 4), Put(m, 5, 5), Put(m, 6, 6),
      new IrForEachCollection(R(m), EnumerationMode.Live, k, null, new IrBlock([Digit(sum, R(k))])),
      new IrReturn(R(sum))));

    // A live loop that inserts and clears: 1, then 3 inserted, then cleared and 5 inserted, is 135.
    var live = new IrLocal("live", insertion);
    var lk = new IrLocal("lk", IrType.I32);
    var lv = new IrLocal("lv", IrType.I32);
    var seen = new IrLocal("seen", IrType.I32);
    functions.Add(F("InsertionLive", [live, lk, lv, seen],
      new IrDeclare(live, Op(IrCollectionOp.MapNew, insertion)),
      new IrDeclare(seen, I(0)),
      Put(live, 1, 1),
      new IrForEachCollection(R(live), EnumerationMode.Live, lk, lv, new IrBlock([
        Digit(seen, R(lv)),
        new IrIf(new IrBinary(IrBinaryOp.Equal, R(lk), I(1)), new IrBlock([Put(live, 3, 3)]), null),
        new IrIf(new IrBinary(IrBinaryOp.Equal, R(lk), I(3)), new IrBlock([Do(IrCollectionOp.MapClear, R(live)), Put(live, 5, 5)]), null)])),
      new IrReturn(R(seen))));

    // Sets of JavaScript's order: add, a second add, remove, contains, count.
    var setType = IrType.Set(IrType.I32, MapOrder.Insertion);
    var set = new IrLocal("set", setType);
    var item = new IrLocal("item", IrType.I32);
    var digits = new IrLocal("digits", IrType.I32);
    functions.Add(F("InsertionSet", [set, item, digits],
      new IrDeclare(set, Op(IrCollectionOp.SetNew, setType)),
      new IrDeclare(digits, I(0)),
      new IrExprStmt(Op(IrCollectionOp.SetAdd, IrType.Bool, R(set), I(3))),
      new IrExprStmt(Op(IrCollectionOp.SetAdd, IrType.Bool, R(set), I(1))),
      new IrIf(Op(IrCollectionOp.SetAdd, IrType.Bool, R(set), I(3)), new IrBlock([new IrReturn(I(-1))]), null),
      new IrExprStmt(Op(IrCollectionOp.SetRemove, IrType.Bool, R(set), I(3))),
      new IrExprStmt(Op(IrCollectionOp.SetAdd, IrType.Bool, R(set), I(3))),
      new IrForEachCollection(R(set), EnumerationMode.Live, item, null, new IrBlock([Digit(digits, R(item))])),
      new IrReturn(Plus(Times(R(digits), 10), Plus(Op(IrCollectionOp.SetCount, IrType.I32, R(set)), Bit(Op(IrCollectionOp.SetContains, IrType.Bool, R(set), I(9))))))));

    // Lookups: a present and a missing key tried, a key added twice, try-add, the trap of a missing key.
    var look = new IrLocal("look", slots);
    var optional = IrType.Optional(IrType.I32);
    functions.Add(F("Lookups", [look],
      new IrDeclare(look, Op(IrCollectionOp.MapNew, slots)),
      Do(IrCollectionOp.MapAdd, R(look), I(1), I(7)),
      new IrIf(Op(IrCollectionOp.MapTryAdd, IrType.Bool, R(look), I(1), I(8)), new IrBlock([new IrReturn(I(-1))]), null),
      new IrIf(new IrUnary(IrUnaryOp.LogicalNot, Op(IrCollectionOp.MapTryAdd, IrType.Bool, R(look), I(2), I(8))), new IrBlock([new IrReturn(I(-2))]), null),
      new IrTry(new IrBlock([Do(IrCollectionOp.MapAdd, R(look), I(2), I(9))]),
        [new IrCatch(IrExceptionClass.ArgumentException, null, null, new IrBlock([Put(look, 3, 3)]))], null),
      new IrTry(new IrBlock([new IrExprStmt(Op(IrCollectionOp.MapGet, IrType.I32, R(look), I(4)))]),
        [new IrCatch(IrExceptionClass.KeyNotFoundException, null, null, new IrBlock([Put(look, 4, 4)]))], null),
      new IrReturn(Plus(Times(new IrCoalesce(Op(IrCollectionOp.MapTryGet, optional, R(look), I(1)), I(0)), 1000),
        Plus(Times(new IrCoalesce(Op(IrCollectionOp.MapTryGet, optional, R(look), I(9)), I(5)), 100),
          Plus(Times(Op(IrCollectionOp.MapGet, IrType.I32, R(look), I(4)), 10), Op(IrCollectionOp.MapCount, IrType.I32, R(look))))))));

    // Object keys by identity, object values tried.
    var box = new IrClassType("Cell");
    var content = new IrField("Content", IrType.I32, false);
    box.Define([content]);
    var make = new IrConstructor("Cell..ctor");
    box.DefineMembers([make], [], [], []);
    var self = new IrLocal("this", IrType.Object(box));
    var start = new IrLocal("start", IrType.I32);
    functions.Add(new IrFunction("Cell..ctor", false, IrType.Void, [self, start], [], new IrBlock([new IrFieldStore(R(self), content, R(start))])));
    var cells = IrType.Map(IrType.Object(box), IrType.Object(box), MapOrder.SlotReuse);
    var byCell = new IrLocal("byCell", cells);
    var first = new IrLocal("first", IrType.Object(box));
    functions.Add(F("ObjectKeys", [byCell, first],
      new IrDeclare(first, new IrNew(IrType.Object(box), make, [I(2)])),
      new IrDeclare(byCell, Op(IrCollectionOp.MapNew, cells)),
      Do(IrCollectionOp.MapPut, R(byCell), R(first), R(first)),
      Do(IrCollectionOp.MapPut, R(byCell), new IrNew(IrType.Object(box), make, [I(2)]), R(first)),
      new IrReturn(Plus(Times(Op(IrCollectionOp.MapCount, IrType.I32, R(byCell)), 100),
        Plus(Times(new IrFieldLoad(Op(IrCollectionOp.MapTryGet, IrType.Object(box), R(byCell), R(first)), content), 10),
          Bit(new IrIsNull(Op(IrCollectionOp.MapTryGet, IrType.Object(box), R(byCell), new IrNew(IrType.Object(box), make, [I(2)])))))))));

    // 64-bit keys, which JavaScript below ECMAScript 2020 holds as pairs of halves.
    var longs = IrType.Set(IrType.I64, MapOrder.SlotReuse);
    var wide = new IrLocal("wide", longs);
    functions.Add(F("LongKeys", [wide],
      new IrDeclare(wide, Op(IrCollectionOp.SetNew, longs)),
      new IrExprStmt(Op(IrCollectionOp.SetAdd, IrType.Bool, R(wide), new IrConst(IrType.I64, 1L << 40))),
      new IrExprStmt(Op(IrCollectionOp.SetAdd, IrType.Bool, R(wide), new IrConst(IrType.I64, 1L << 40))),
      new IrExprStmt(Op(IrCollectionOp.SetAdd, IrType.Bool, R(wide), new IrConst(IrType.I64, 1))),
      new IrReturn(Plus(Times(Op(IrCollectionOp.SetCount, IrType.I32, R(wide)), 10), Bit(Op(IrCollectionOp.SetContains, IrType.Bool, R(wide), new IrConst(IrType.I64, 1L << 40)))))));

    // binary32 keys: -0 kept, every NaN one key.
    var singles = IrType.Map(IrType.F32, IrType.I32, MapOrder.SlotReuse);
    var single = new IrLocal("single", singles);
    var keyed = new IrLocal("keyed", IrType.F32);
    var sign = new IrLocal("sign", IrType.I32);
    var negativeZero = new IrConst(IrType.F32, IrOperators.Bits(IrType.F32, -0.0));
    var nan = new IrConst(IrType.F32, IrOperators.Bits(IrType.F32, double.NaN));
    functions.Add(F("SingleKeys", [single, keyed, sign],
      new IrDeclare(single, Op(IrCollectionOp.MapNew, singles)),
      new IrDeclare(sign, I(0)),
      Do(IrCollectionOp.MapPut, R(single), negativeZero, I(1)),
      Do(IrCollectionOp.MapPut, R(single), new IrConst(IrType.F32, 0), I(2)),
      Do(IrCollectionOp.MapPut, R(single), nan, I(3)),
      Do(IrCollectionOp.MapPut, R(single), new IrBinary(IrBinaryOp.Divide, new IrConst(IrType.F32, 0), new IrConst(IrType.F32, 0)), I(4)),
      new IrForEachCollection(R(single), EnumerationMode.Checked, keyed, null, new IrBlock([
        new IrIf(new IrBinary(IrBinaryOp.Less, new IrBinary(IrBinaryOp.Divide, new IrConst(IrType.F32, IrOperators.Bits(IrType.F32, 1.0)), R(keyed)), new IrConst(IrType.F32, 0)),
          new IrBlock([new IrAssign(sign, Plus(R(sign), I(100)))]), null)])),
      new IrReturn(Plus(R(sign), Plus(Times(Op(IrCollectionOp.MapCount, IrType.I32, R(single)), 10), Op(IrCollectionOp.MapGet, IrType.I32, R(single), nan))))));

    // Struct values are copied out of a map.
    var point = new IrStructType("Spot");
    var x = new IrField("X", IrType.I32, false);
    point.Define([x]);
    var spots = IrType.Map(IrType.String, IrType.Struct(point), MapOrder.Insertion);
    var where = new IrLocal("where", spots);
    var spot = new IrLocal("spot", IrType.Struct(point));
    functions.Add(F("StructValues", [where, spot],
      new IrDeclare(where, Op(IrCollectionOp.MapNew, spots)),
      Do(IrCollectionOp.MapPut, R(where), new IrStrConst("a"), new IrStructNew(IrType.Struct(point), [I(1)])),
      new IrDeclare(spot, Op(IrCollectionOp.MapGet, IrType.Struct(point), R(where), new IrStrConst("a"))),
      new IrFieldStore(R(spot), x, I(5)),
      new IrReturn(Plus(Times(new IrFieldLoad(Op(IrCollectionOp.MapGet, IrType.Struct(point), R(where), new IrStrConst("a")), x), 10),
        new IrFieldLoad(new IrUnwrap(Op(IrCollectionOp.MapTryGet, IrType.Optional(IrType.Struct(point)), R(where), new IrStrConst("a"))), x)))));

    // Loops over the values alone and over the keys alone, checked.
    var values = new IrLocal("values", slots);
    var value = new IrLocal("value", IrType.I32);
    var other = new IrLocal("other", IrType.I32);
    var total = new IrLocal("total", IrType.I32);
    functions.Add(F("ValuesOnly", [values, value, other, total],
      new IrDeclare(values, Op(IrCollectionOp.MapNew, slots)),
      new IrDeclare(total, I(0)),
      Put(values, 1, 5), Put(values, 2, 6),
      new IrForEachCollection(R(values), EnumerationMode.Checked, null, value, new IrBlock([Digit(total, R(value))])),
      new IrForEachCollection(R(values), EnumerationMode.Checked, other, null, new IrBlock([Digit(total, R(other))])),
      new IrReturn(R(total))));

    // A live loop over .NET's order, removed and taken again in the loop: the freed slot is taken.
    var slotted = new IrLocal("slotted", slots);
    var at = new IrLocal("at", IrType.I32);
    var walked = new IrLocal("walked", IrType.I32);
    functions.Add(F("SlotsLive", [slotted, at, walked],
      new IrDeclare(slotted, Op(IrCollectionOp.MapNew, slots)),
      new IrDeclare(walked, I(0)),
      Put(slotted, 1, 1), Put(slotted, 2, 2), Put(slotted, 3, 3),
      new IrForEachCollection(R(slotted), EnumerationMode.Live, at, null, new IrBlock([
        Digit(walked, R(at)),
        new IrIf(new IrBinary(IrBinaryOp.Equal, R(at), I(2)), new IrBlock([Remove(slotted, 1), Put(slotted, 9, 9)]), null)])),
      new IrReturn(R(walked))));

    return new IrModule("Maps", functions) { Types = [box, point] };
  }

  /// <summary>The calls of <see cref="Maps"/>.</summary>
  public static IEnumerable<PrototypeCall> MapCalls() => MapFunctions.Select(function => new PrototypeCall(function, []) { Returns = typeof(int) });

}
