using System;
using NUnit.Framework;
using Polypiler.Core.Backends.Cpp;
using Polypiler.Core.IR;
using static Polypiler.Tests.Backends.Cpp.CppTestHarness;

namespace Polypiler.Tests.Backends.Cpp;

/// <summary>
/// Live g++ tests for arrays, endian loads/stores, strings, structs, enums, optionals, lists,
/// module constants, globals, and module initializers across C++17, C++20, and C++23.
/// </summary>
[TestFixture]
public sealed class CppMemoryAndTypesTests {

  [Test]
  public void ArraysEndianAndGlobals_ParityAcrossAllStandards() {
    var tableConst = new IrModuleConst("Table", IrType.Array(IrType.I32), [10, 20, 30]);
    var scalarConst = new IrModuleConst("Magic", IrType.I32, [7]);
    var strConst = IrModuleConst.OfStrings("Greeting", IrType.String, ["hi\0there"]);
    var strArrConst = IrModuleConst.OfStrings("Words", IrType.Array(IrType.String), ["a", "b"]);
    var counter = new IrGlobal("counter", IrType.I32, I(5));
    var emptyGlobal = new IrGlobal("flag", IrType.Bool, null);

    var buf = L("buf", IrType.Array(IrType.U8));
    var dst = L("dst", IrType.Array(IrType.I32));
    var tmp = L("tmp", IrType.Array(IrType.I32));
    var n = L("n", IrType.I32);
    var reassigned = L("a", IrType.Array(IrType.I32));

    var initFn = Function("init", false, IrType.Void, [],
      new IrGlobalAssign(counter, Bin(IrBinaryOp.Add, new IrGlobalRef(counter), I(10))));

    var mod = new IrModule("MemOps", [
      initFn,
      Function("readConsts", IrType.I32, [],
        new IrReturn(Bin(IrBinaryOp.Add,
          Bin(IrBinaryOp.Add, new IrArrayLoad(new IrConstRef(tableConst), I(1)), new IrConstRef(scalarConst)),
          Bin(IrBinaryOp.Add, new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [new IrConstRef(strConst)]),
            new IrArrayLength(new IrConstRef(strArrConst)))))),
      Function("bumpCounter", IrType.I32, [],
        new IrGlobalAssign(counter, Bin(IrBinaryOp.Add, new IrGlobalRef(counter), new IrConditional(new IrGlobalRef(emptyGlobal), I(10), I(1)))),
        new IrReturn(new IrGlobalRef(counter))),
      Function("makeAndCopy", IrType.I32, [n],
        new IrDeclare(tmp, new IrNewArray(IrType.Array(IrType.I32), R(n))),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayFill, IrType.Void, [R(tmp), I(3)])),
        new IrDeclare(dst, new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.I32), [I(1), I(2), I(3), I(4)])),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayCopy, IrType.Void, [R(dst), I(0), R(dst), I(1), I(3)])),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayCopy, IrType.Void, [R(dst), I(1), R(dst), I(0), I(2)])),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayClear, IrType.Void, [R(tmp)])),
        new IrReturn(Bin(IrBinaryOp.Add, new IrArrayLoad(R(dst), I(0)), new IrArrayLoad(R(tmp), I(0))))),
      Function("mutateParam", IrType.I32, [dst, n],
        new IrStore(R(dst), I(0), R(n)),
        new IrReturn(new IrArrayLoad(R(dst), I(0)))),
      Function("callWithRvalue", IrType.I32, [],
        new IrReturn(new IrCall("mutateParam", IrType.I32, [new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.I32), [I(1), I(2)]), I(99)]))),
      Function("copyIntoParam", IrType.I32, [dst],
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayCopy, IrType.Void, [new IrConstRef(tableConst), I(0), R(dst), I(0), I(2)])),
        new IrReturn(new IrArrayLoad(R(dst), I(1)))),
      Function("narrowTypesAndDefaults", IrType.I32, [L("s8", IrType.I8), L("s16", IrType.I16)],
        new IrDeclare(L("f1", IrType.F32), null),
        new IrDeclare(L("d1", IrType.F64), null),
        new IrDeclare(L("o1", IrType.Optional(IrType.I32)), null),
        new IrDeclare(L("st", IrType.String), null),
        new IrDeclare(L("b8", IrType.I8), C(IrType.I8, -5)),
        new IrDeclare(L("u8v", IrType.U8), C(IrType.U8, 200)),
        new IrDeclare(L("b16", IrType.I16), C(IrType.I16, -300)),
        new IrDeclare(L("esc", IrType.String), new IrStrConst("a\\b\"c\nd\re\tf")),
        new IrReturn(Bin(IrBinaryOp.Add,
          Bin(IrBinaryOp.Add, new IrConvert(IrType.I32, R(L("s8", IrType.I8))), new IrConvert(IrType.I32, R(L("s16", IrType.I16)))),
          Bin(IrBinaryOp.Add,
            Bin(IrBinaryOp.Add, new IrConvert(IrType.I32, R(L("b8", IrType.I8))), new IrConvert(IrType.I32, R(L("b16", IrType.I16)))),
            Bin(IrBinaryOp.Add, new IrConvert(IrType.I32, R(L("u8v", IrType.U8))),
              Bin(IrBinaryOp.Add, new IrConvert(IrType.I32, R(L("f1", IrType.F32)), IrConversion.Saturate),
                Bin(IrBinaryOp.Add, new IrConvert(IrType.I32, R(L("d1", IrType.F64)), IrConversion.Saturate),
                  Bin(IrBinaryOp.Add, new IrCoalesce(R(L("o1", IrType.Optional(IrType.I32))), I(0)),
                    Bin(IrBinaryOp.Add, new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [R(L("st", IrType.String))]),
                      new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [R(L("esc", IrType.String))])))))))))),
      Function("reassignParam", IrType.I32, [reassigned],
        new IrAssign(reassigned, new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.I32), [I(77)])),
        new IrReturn(new IrArrayLoad(R(reassigned), I(0)))),
      Function("endianRoundTrip", IrType.I64, [buf],
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.StoreLittle, IrType.Void, [R(buf), I(0), U(0x12345678u)])),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.StoreBig, IrType.Void, [R(buf), I(4), F32(1.5f)])),
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrConvert(IrType.I64, new IrIntrinsic(IrIntrinsicOp.LoadLittle, IrType.U32, [R(buf), I(0)])),
          new IrConvert(IrType.I64, new IrIntrinsic(IrIntrinsicOp.FloatBits, IrType.I32, [new IrIntrinsic(IrIntrinsicOp.LoadBig, IrType.F32, [R(buf), I(4)])])))))
    ], [tableConst, scalarConst, strConst, strArrConst]) {
      Globals = [counter, emptyGlobal],
      Initializer = "init"
    };

    var driver = """
      #include "mem_ops.hpp"
      #include <cassert>
      #include <stdexcept>
      #include <vector>
      using namespace mem_ops;
      int main() {
        assert(read_consts() == (20 + 7 + 8 + 2));
        assert(bump_counter() == 16);
        assert(bump_counter() == 17);
        assert(make_and_copy(4) == 1);
        bool negCaught = false;
        try { (void)make_and_copy(-1); } catch (const std::overflow_error&) { negCaught = true; }
        assert(negCaught);

        assert(call_with_rvalue() == 99);
        std::vector<std::int32_t> cpDst{0, 0};
        assert(copy_into_param(cpDst) == 20);
        assert(narrow_types_and_defaults(static_cast<std::int8_t>(10), static_cast<std::int16_t>(20)) == (10 + 20 - 5 - 300 + 200 + 11));
        std::vector<std::int32_t> orig{1, 2};
        assert(reassign_param(orig) == 77 && orig[0] == 1);

        std::vector<std::uint8_t> buf(8, 0);
        const std::int64_t expected = static_cast<std::int64_t>(0x12345678u) + static_cast<std::int64_t>(0x3FC00000);
        assert(endian_round_trip(buf) == expected);
        assert(buf[0] == 0x78 && buf[3] == 0x12);
        std::vector<std::uint8_t> small(2, 0);
        bool oob = false;
        try { (void)endian_round_trip(small); } catch (const std::out_of_range&) { oob = true; }
        assert(oob);
        return 0;
      }
      """;
    CompileAndRunAllStandards(mod, driver);
  }

  [Test]
  public void StructsEnumsOptionalsAndLists_ParityAcrossAllStandards() {
    var pt = new IrStructType("Point", [new IrField("X", IrType.F64, false), new IrField("Y", IrType.I32, false)]) { IsEquatable = true };
    var emptySt = new IrStructType("Empty", []) { IsEquatable = true };
    var color = new IrEnumType("Color", IrType.I32, [new("Red", 1), new("Green", 2), new("Blue", 4)], isFlags: true) { IsPublic = true };

    var p1 = L("p1", IrType.Struct(pt));
    var p2 = L("p2", IrType.Struct(pt));
    var e1 = L("e1", IrType.Struct(emptySt));
    var c1 = L("c1", color.Type);
    var c2 = L("c2", color.Type);
    var opt = L("opt", IrType.Optional(IrType.I32));
    var opt2 = L("opt2", IrType.Optional(IrType.I32));
    var list = L("list", IrType.List(IrType.I32));
    var elem = L("elem", IrType.I32);
    var sum = L("sum", IrType.I32);

    var mod = new IrModule("DataTypes", [
      Function("structOps", IrType.Bool, [p1],
        new IrDeclare(p2, new IrStructDefault(IrType.Struct(pt))),
        new IrFieldStore(R(p2), pt.Fields[0], new IrFieldLoad(R(p1), pt.Fields[0])),
        new IrFieldStore(R(p2), pt.Fields[1], new IrFieldLoad(R(p1), pt.Fields[1])),
        new IrDeclare(e1, new IrStructNew(IrType.Struct(emptySt), [])),
        new IrReturn(Bin(IrBinaryOp.LogicalAnd,
          new IrStructEquals(R(p1), R(p2)),
          new IrStructEquals(R(e1), new IrStructDefault(IrType.Struct(emptySt)))))),

      Function("enumOps", IrType.I32, [c1, c2],
        new IrDeclare(sum, I(0)),
        new IrIf(new IrIntrinsic(IrIntrinsicOp.EnumHasFlag, IrType.Bool, [Bin(IrBinaryOp.Or, R(c1), R(c2)), R(c2)]),
          Block(new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), I(1)))), null),
        new IrIf(new IrIntrinsic(IrIntrinsicOp.EnumIsDefined, IrType.Bool, [R(c1)]),
          Block(new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), I(2)))), null),
        new IrIf(new IrIntrinsic(IrIntrinsicOp.StrEquals, IrType.Bool, [new IrIntrinsic(IrIntrinsicOp.EnumToString, IrType.String, [R(c1)]), new IrStrConst("Red")]),
          Block(new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), I(4)))), null),
        new IrIf(Bin(IrBinaryOp.Equal, new IrIntrinsic(IrIntrinsicOp.EnumParse, color.Type, [new IrStrConst("Green"), B(false)]), C(color.Type, 2)),
          Block(new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), I(8)))), null),
        new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), new IrArrayLength(new IrIntrinsic(IrIntrinsicOp.EnumNames, IrType.Array(IrType.String), [R(c1)])))),
        new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), new IrArrayLength(new IrIntrinsic(IrIntrinsicOp.EnumValues, IrType.Array(color.Type), [R(c1)])))),
        new IrReturn(R(sum))),

      Function("enumUnknownConst", color.Type, [],
        new IrDeclare(L("defC", color.Type), null),
        new IrDeclare(L("raw", IrType.I32), new IrConvert(IrType.I32, new IrConvert(IrType.I32, R(L("defC", color.Type))))),
        new IrReturn(new IrConvert(color.Type, Bin(IrBinaryOp.Add, R(L("raw", IrType.I32)), I(99))))),

      Function("mutateStructArrAndList", IrType.I32, [L("pts", IrType.Array(IrType.Struct(pt))), L("xs", IrType.List(IrType.I32))],
        new IrFieldStore(new IrArrayLoad(R(L("pts", IrType.Array(IrType.Struct(pt)))), I(0)), pt.Fields[1], I(55)),
        new IrExprStmt(new IrCollection(IrCollectionOp.ListAdd, IrType.Void, [R(L("xs", IrType.List(IrType.I32))), I(12)])),
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrFieldLoad(new IrArrayLoad(R(L("pts", IrType.Array(IrType.Struct(pt)))), I(0)), pt.Fields[1]),
          new IrCollection(IrCollectionOp.ListCount, IrType.I32, [R(L("xs", IrType.List(IrType.I32)))])))),

      Function("optOps", IrType.I32, [opt, opt2],
        new IrIf(new IrIsNone(R(opt)),
          Block(new IrReturn(new IrCoalesce(new IrCoalesce(R(opt), R(opt2)), I(77)))),
          Block(new IrReturn(Bin(IrBinaryOp.Add, new IrUnwrap(R(opt)) { IsTested = true }, new IrUnwrap(new IrSome(I(1)))))))),

      Function("optUnwrapMissing", IrType.I32, [], new IrReturn(new IrUnwrap(new IrNone(IrType.Optional(IrType.I32))))),

      Function("listOps", IrType.I32, [],
        new IrDeclare(list, new IrCollection(IrCollectionOp.ListOf, IrType.List(IrType.I32), [I(10), I(20)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.ListAdd, IrType.Void, [R(list), I(30)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.ListInsert, IrType.Void, [R(list), I(1), I(15)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.ListSet, IrType.Void, [R(list), I(0), I(5)])),
        new IrExprStmt(new IrCollection(IrCollectionOp.ListRemoveAt, IrType.Void, [R(list), I(2)])),
        new IrDeclare(sum, new IrCoalesce(new IrCollection(IrCollectionOp.ListRemoveLast, IrType.Optional(IrType.I32), [R(list)]), I(0))),
        new IrForEachCollection(R(list), EnumerationMode.Live, elem, null, Block(
          new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), R(elem))))),
        new IrIf(new IrCollection(IrCollectionOp.ListContains, IrType.Bool, [R(list), I(15)]),
          Block(new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), new IrCollection(IrCollectionOp.ListIndexOfEquals, IrType.I32, [R(list), I(15)])))), null),
        new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), new IrCollection(IrCollectionOp.ListCount, IrType.I32, [R(list)]))),
        new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), new IrCollection(IrCollectionOp.ListGet, IrType.I32, [R(list), I(0)]))),
        new IrDeclare(L("copy", IrType.Array(IrType.I32)), new IrCollection(IrCollectionOp.ListCopyToArray, IrType.Array(IrType.I32), [R(list)])),
        new IrDeclare(L("fromArr", IrType.List(IrType.I32)), new IrCollection(IrCollectionOp.ListFromArray, IrType.List(IrType.I32), [R(L("copy", IrType.Array(IrType.I32)))])),
        new IrExprStmt(new IrCollection(IrCollectionOp.ListClear, IrType.Void, [R(list)])),
        new IrDeclare(L("emptyList", IrType.List(IrType.I32)), new IrCollection(IrCollectionOp.ListNew, IrType.List(IrType.I32), [])),
        new IrReturn(Bin(IrBinaryOp.Add, R(sum),
          Bin(IrBinaryOp.Add, new IrCollection(IrCollectionOp.ListCount, IrType.I32, [R(L("fromArr", IrType.List(IrType.I32)))]),
            new IrCollection(IrCollectionOp.ListCount, IrType.I32, [R(L("emptyList", IrType.List(IrType.I32)))])))))
    ]) { Types = [pt, emptySt, color] };

    var expectedList = (int)Eval(mod, "listOps")!;

    var driver = $$"""
      #include "data_types.hpp"
      #include <cassert>
      #include <limits>
      #include <optional>
      #include <stdexcept>
      using namespace data_types;
      int main() {
        Point p{std::numeric_limits<double>::quiet_NaN(), 42};
        assert(struct_ops(p) == true);
        assert(!(p != p));
        assert(enum_ops(Color::Red, Color::Green) == (1 + 2 + 4 + 8 + 3 + 3));
        assert(static_cast<std::int32_t>(enum_unknown_const()) == 99);
        std::vector<Point> pts{ Point{1.0, 2} };
        std::vector<std::int32_t> xs{1};
        assert(mutate_struct_arr_and_list(pts, xs) == 57);
        assert(opt_ops(std::optional<std::int32_t>(10), std::nullopt) == 11);
        assert(opt_ops(std::nullopt, std::optional<std::int32_t>(25)) == 25);
        assert(opt_ops(std::nullopt, std::nullopt) == 77);
        bool missing = false;
        try { (void)opt_unwrap_missing(); } catch (const std::logic_error&) { missing = true; }
        assert(missing);
        assert(list_ops() == {{expectedList}});
        return 0;
      }
      """;
    CompileAndRunAllStandards(mod, driver);
  }

  [Test]
  public void StringIntrinsics_ParityAcrossAllStandards() {
    var s = L("s", IrType.String);
    var t = L("t", IrType.String);
    var reassigned = L("rs", IrType.String);

    var mod = new IrModule("StrOps", [
      Function("basic", IrType.I32, [s, t],
        new IrDeclare(L("cat", IrType.String), new IrIntrinsic(IrIntrinsicOp.StrConcat, IrType.String, [R(s), new IrStrConst("-"), R(t)])),
        new IrDeclare(L("ch", IrType.U16), new IrIntrinsic(IrIntrinsicOp.StrCodeUnitAt, IrType.U16, [R(s), I(0)])),
        new IrDeclare(L("one", IrType.String), new IrIntrinsic(IrIntrinsicOp.StrFromCodeUnit, IrType.String, [R(L("ch", IrType.U16))])),
        new IrDeclare(L("units", IrType.Array(IrType.U16)), new IrIntrinsic(IrIntrinsicOp.StrToCodeUnits, IrType.Array(IrType.U16), [R(s)])),
        new IrDeclare(L("back", IrType.String), new IrIntrinsic(IrIntrinsicOp.StrFromCodeUnits, IrType.String, [R(L("units", IrType.Array(IrType.U16)))])),
        new IrDeclare(L("slice", IrType.String), new IrIntrinsic(IrIntrinsicOp.StrFromCodeUnits, IrType.String, [R(L("units", IrType.Array(IrType.U16))), I(1), I(2)])),
        new IrDeclare(L("rep", IrType.String), new IrIntrinsic(IrIntrinsicOp.StrRepeat, IrType.String, [R(L("one", IrType.String)), I(3)])),
        new IrDeclare(L("sub1", IrType.String), new IrIntrinsic(IrIntrinsicOp.StrSubstring, IrType.String, [R(L("cat", IrType.String)), I(2)])),
        new IrDeclare(L("sub2", IrType.String), new IrIntrinsic(IrIntrinsicOp.StrSubstring, IrType.String, [R(L("cat", IrType.String)), I(0), I(3)])),
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [R(L("rep", IrType.String))]),
          Bin(IrBinaryOp.Add,
            new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [R(L("sub1", IrType.String))]),
            Bin(IrBinaryOp.Add,
              new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [R(L("sub2", IrType.String))]),
              Bin(IrBinaryOp.Add,
                new IrConditional(new IrIntrinsic(IrIntrinsicOp.StrEquals, IrType.Bool, [R(s), R(L("back", IrType.String))]), I(10), I(0)),
                new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [R(L("slice", IrType.String))]))))))),

      Function("searchAndTransform", IrType.I32, [s],
        new IrDeclare(L("score", IrType.I32), I(0)),
        new IrAssign(L("score", IrType.I32), Bin(IrBinaryOp.Add, R(L("score", IrType.I32)), new IrIntrinsic(IrIntrinsicOp.StrCompareOrdinal, IrType.I32, [R(s), new IrStrConst("zzz")]))),
        new IrAssign(L("score", IrType.I32), Bin(IrBinaryOp.Add, R(L("score", IrType.I32)), new IrIntrinsic(IrIntrinsicOp.StrIndexOf, IrType.I32, [R(s), new IrStrConst("ab")]))),
        new IrAssign(L("score", IrType.I32), Bin(IrBinaryOp.Add, R(L("score", IrType.I32)), new IrIntrinsic(IrIntrinsicOp.StrIndexOf, IrType.I32, [R(s), C(IrType.U16, 'b'), I(2)]))),
        new IrAssign(L("score", IrType.I32), Bin(IrBinaryOp.Add, R(L("score", IrType.I32)), new IrIntrinsic(IrIntrinsicOp.StrLastIndexOf, IrType.I32, [R(s), new IrStrConst("ab")]))),
        new IrAssign(L("score", IrType.I32), Bin(IrBinaryOp.Add, R(L("score", IrType.I32)), new IrIntrinsic(IrIntrinsicOp.StrLastIndexOf, IrType.I32, [R(s), new IrStrConst("ab"), I(3)]))),
        new IrIf(new IrIntrinsic(IrIntrinsicOp.StrStartsWith, IrType.Bool, [R(s), new IrStrConst("ab")]),
          Block(new IrAssign(L("score", IrType.I32), Bin(IrBinaryOp.Add, R(L("score", IrType.I32)), I(100)))), null),
        new IrIf(new IrIntrinsic(IrIntrinsicOp.StrEndsWith, IrType.Bool, [R(s), new IrStrConst("ab")]),
          Block(new IrAssign(L("score", IrType.I32), Bin(IrBinaryOp.Add, R(L("score", IrType.I32)), I(200)))), null),
        new IrIf(new IrIntrinsic(IrIntrinsicOp.StrContains, IrType.Bool, [R(s), C(IrType.U16, 'c')]),
          Block(new IrAssign(L("score", IrType.I32), Bin(IrBinaryOp.Add, R(L("score", IrType.I32)), I(400)))), null),
        new IrReturn(R(L("score", IrType.I32)))),

      Function("replaceSplitTrimPad", IrType.String, [reassigned],
        new IrAssign(reassigned, new IrIntrinsic(IrIntrinsicOp.StrTrim, IrType.String, [R(reassigned), StringOperations.Set(WhitespaceSet.DotNet)])),
        new IrAssign(reassigned, new IrIntrinsic(IrIntrinsicOp.StrTrimStart, IrType.String, [R(reassigned), StringOperations.Set(WhitespaceSet.DotNet)])),
        new IrAssign(reassigned, new IrIntrinsic(IrIntrinsicOp.StrTrimEnd, IrType.String, [R(reassigned), StringOperations.Set(WhitespaceSet.DotNet)])),
        new IrAssign(reassigned, new IrIntrinsic(IrIntrinsicOp.StrReplaceAll, IrType.String, [R(reassigned), new IrStrConst(","), new IrStrConst(";")])),
        new IrAssign(reassigned, new IrIntrinsic(IrIntrinsicOp.StrReplaceFirst, IrType.String, [R(reassigned), new IrStrConst(";"), new IrStrConst(":")])),
        new IrDeclare(L("parts", IrType.Array(IrType.String)), new IrIntrinsic(IrIntrinsicOp.StrSplit, IrType.Array(IrType.String), [R(reassigned), new IrStrConst(":"), I(-1)])),
        new IrDeclare(L("joined", IrType.String), new IrIntrinsic(IrIntrinsicOp.StrJoin, IrType.String, [new IrStrConst("|"), R(L("parts", IrType.Array(IrType.String)))])),
        new IrDeclare(L("p1", IrType.String), new IrIntrinsic(IrIntrinsicOp.StrPadStart, IrType.String, [R(L("joined", IrType.String)), I(10), new IrStrConst("0")])),
        new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrPadEnd, IrType.String, [R(L("p1", IrType.String)), I(12), new IrStrConst("_")])))
    ]);

    var expectedBasic = (int)Eval(mod, "basic", "abc", "xy")!;
    var expectedSearch = (int)Eval(mod, "searchAndTransform", "abcab")!;
    var expectedTransform = (string)Eval(mod, "replaceSplitTrimPad", "  a,b,c  ")!;

    var driver = $$"""
      #include "str_ops.hpp"
      #include <cassert>
      #include <string>
      using namespace str_ops;
      int main() {
        assert(basic("abc", "xy") == {{expectedBasic}});
        assert(search_and_transform("abcab") == {{expectedSearch}});
        assert(replace_split_trim_pad("  a,b,c  ") == "{{expectedTransform}}");
        return 0;
      }
      """;
    CompileAndRunAllStandards(mod, driver);
  }

}
