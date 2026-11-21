using System;
using System.Collections.Generic;
using NUnit.Framework;
using Polypiler.Core.Backends.Perl;
using Polypiler.Core.IR;
using static Polypiler.Tests.Backends.Perl.PerlIr;

namespace Polypiler.Tests.Backends.Perl;

/// <summary>Comprehensive coverage tests for all remaining branches of <c>Polypiler.Core.Backends.Perl</c>.</summary>
[TestFixture]
public sealed class PerlBackendCoverageTests {

  [Test]
  public void Globals_Constants_Initializer_AndEnumClashPrefixing() {
    var e1 = new IrEnumType("First", IrType.I32, [new("Shared", 0), new("OnlyOne", 1)]) { IsPublic = true };
    var e2 = new IrEnumType("Second", IrType.I32, [new("Shared", 2)]) { IsPublic = true };
    var emptyEnum = new IrEnumType("Empty", IrType.I32, []) { IsPublic = true };

    var cStr = IrModuleConst.OfStrings("Greeting", IrType.String, ["hi"]);
    var cStrArr = IrModuleConst.OfStrings("Words", IrType.Array(IrType.String), ["a", "b"]);
    var cBytes = new IrModuleConst("Magic", IrType.Array(IrType.U8), [0xDE, 0xAD]);
    var cNums = new IrModuleConst("Table", IrType.Array(IrType.I32), [1, 2]);
    var cNum = new IrModuleConst("Limit", IrType.I32, [99]);

    var gInit = new IrGlobal("Counter", IrType.I32, I(5));
    var gDef = new IrGlobal("Flag", IrType.Bool, null);
    var initFn = Function("Init", false, IrType.Void, [], new IrGlobalAssign(gDef, B(true)), new IrReturn(null));
    var readFn = Function("ReadAll", IrType.I32, [],
      new IrDeclare(L("s", IrType.String), new IrConstRef(cStr)),
      new IrDeclare(L("sa", IrType.String), new IrArrayLoad(new IrConstRef(cStrArr), I(0))),
      new IrDeclare(L("mb", IrType.U8), new IrArrayLoad(new IrConstRef(cBytes), I(0))),
      new IrDeclare(L("tn", IrType.I32), new IrArrayLoad(new IrConstRef(cNums), I(0))),
      new IrGlobalAssign(gInit, Bin(IrBinaryOp.Add, new IrGlobalRef(gInit), new IrConstRef(cNum))),
      new IrReturn(new IrGlobalRef(gInit)));

    var mod = new IrModule("Full", [initFn, readFn], [cStr, cStrArr, cBytes, cNums, cNum]) {
      Types = [e1, e2, emptyEnum],
      Globals = [gInit, gDef],
      Initializer = "Init"
    };

    var srcArr = Emit(mod, PerlProfile.Default);
    Assert.That(srcArr, Does.Contain("FIRST_SHARED => 0"));
    Assert.That(srcArr, Does.Contain("SECOND_SHARED => 2"));
    Assert.That(srcArr, Does.Contain("ONLY_ONE => 1"));
    Assert.That(srcArr, Does.Contain("_init();"));

    var srcStr = Emit(mod, PerlProfile.Default with { ByteStorage = PerlByteStorage.String });
    Assert.That(srcStr, Does.Contain("pack('H*', 'dead')"));
  }

  [Test]
  public void Classes_Methods_StructsNestedEquals_AndClassRefusals() {
    var inner = new IrStructType("Inner");
    var fD = new IrField("D", IrType.F64, false);
    var fB = new IrField("B", IrType.Bool, false);
    inner.Define([fD, fB]);

    var outer = new IrStructType("Outer");
    var fIn = new IrField("In", IrType.Struct(inner), false);
    var fI = new IrField("N", IrType.I32, false);
    outer.Define([fIn, fI]);

    var cls = new IrClassType("Box");
    var fVal = new IrField("Val", IrType.I32, false);
    cls.Define([fVal]);
    var ctor = new IrConstructor("Box__ctor");
    var mGet = new IrMethod("Get", "Box__get", false);
    var mStatic = new IrMethod("Zero", "Box__zero", true);
    cls.DefineMembers([ctor], [mGet, mStatic], [], []);

    var self = L("self", IrType.Object(cls));
    var v = L("v", IrType.I32);
    var ctorFn = Function("Box__ctor", false, IrType.Void, [self, v],
      new IrFieldStore(R(self), fVal, R(v)), new IrReturn(null));
    var getFn = Function("Box__get", false, IrType.I32, [self],
      new IrReturn(new IrFieldLoad(R(self), fVal)));
    var zeroFn = Function("Box__zero", false, IrType.I32, [], new IrReturn(I(0)));

    var o1 = L("o1", IrType.Struct(outer));
    var o2 = L("o2", IrType.Struct(outer));
    var b1 = L("b1", IrType.Object(cls));
    var b2 = L("b2", IrType.Object(cls));
    var useFn = Function("Use", IrType.I32, [],
      new IrDeclare(o1, new IrStructDefault(IrType.Struct(outer))),
      new IrDeclare(o2, new IrStructNew(IrType.Struct(outer), [new IrStructNew(IrType.Struct(inner), [F64(1.5), B(true)]), I(2)])),
      new IrDeclare(L("eq", IrType.Bool), new IrStructEquals(R(o1), R(o2))),
      new IrDeclare(b1, new IrNew(IrType.Object(cls), ctor, [I(7)])),
      new IrDeclare(b2, new IrNull(IrType.Object(cls))),
      new IrDeclare(L("isn", IrType.Bool), new IrIsNull(R(b2))),
      new IrDeclare(L("refeq", IrType.Bool), new IrReferenceEquals(R(b1), R(b2))),
      new IrDeclare(L("g1", IrType.I32), new IrMethodCall(IrType.I32, mGet, R(b1), [])),
      new IrDeclare(L("g2", IrType.I32), new IrMethodCall(IrType.I32, mStatic, null, [])),
      new IrReturn(Bin(IrBinaryOp.Add, R(L("g1", IrType.I32)), R(L("g2", IrType.I32)))));

    var mod = new IrModule("Objs", [ctorFn, getFn, zeroFn, useFn]) { Types = [inner, outer, cls] };
    var src = Emit(mod);
    Assert.That(src, Does.Contain("sub _eq_outer"));
    Assert.That(src, Does.Contain("sub _eq_inner"));
    Assert.That(src, Does.Contain("_box__ctor"));
    Assert.That(src, Does.Contain("_box__get"));
    Assert.That(src, Does.Contain("_box__zero"));

    // Virtual class & inheritance refusals -> PLY1782
    var baseCls = new IrClassType("Base");
    baseCls.Define([]);
    var virtMethod = new IrMethod("Run", "Base__run", false) { Dispatch = IrDispatch.Virtual };
    baseCls.DefineMembers([], [virtMethod], [], []);
    var derived = new IrClassType("Derived");
    derived.Inherit(baseCls, []);
    derived.Define([]);
    derived.DefineMembers([], [], [], []);
    var iface = new IrInterfaceType("Iface");
    iface.DefineMembers([], [], []);
    var implCls = new IrClassType("Impl");
    implCls.Inherit(null, [iface]);
    implCls.Define([]);
    implCls.DefineMembers([], [], [], []);
    var sParam = L("s", IrType.Object(baseCls));
    var badMod = new IrModule("BadOO", [
      Function("Base__run", false, IrType.I32, [L("self", IrType.Object(baseCls))], new IrReturn(I(0))),
      Function("f", IrType.I32, [sParam],
        new IrDeclare(L("t", IrType.Bool), new IrTypeTest(R(sParam), IrType.Object(derived))),
        new IrReturn(new IrMethodCall(IrType.I32, virtMethod, R(sParam), []) { Kind = IrMethodCallKind.Virtual }))
    ]) { Types = [baseCls, derived, iface, implCls] };
    var probs = Problems(badMod);
    Assert.That(probs, Has.Some.Contains("PLY1782"));
  }

  [Test]
  public void AllScalarIntrinsics_Steps_FloatSpecials_AndU64Ops() {
    var x = L("x", IrType.F64);
    var y = L("y", IrType.F64);
    var f32 = L("f", IrType.F32);
    var i64 = L("i64", IrType.I64);
    var u64 = L("u64", IrType.U64);
    var i32 = L("i32", IrType.I32);
    var u32 = L("u32", IrType.U32);
    var arr = L("arr", IrType.Array(IrType.I32));
    var fvar = L("fv", IrType.F64);

    var fn = Function("scalars", IrType.I32, [x, y, f32, i64, u64, i32, u32, arr],
      new IrDeclare(L("f32inf", IrType.F32), F32(float.PositiveInfinity)),
      new IrDeclare(L("f32ninf", IrType.F32), F32(float.NegativeInfinity)),
      new IrDeclare(L("f32nan", IrType.F32), F32(float.NaN)),
      new IrDeclare(L("f32val", IrType.F32), F32(1.25f)),
      new IrDeclare(L("fneg", IrType.F64), new IrUnary(IrUnaryOp.Negate, R(x))),
      new IrDeclare(L("fsub", IrType.F64), Bin(IrBinaryOp.Subtract, R(x), R(y))),
      new IrDeclare(L("fmul", IrType.F64), Bin(IrBinaryOp.Multiply, R(x), R(y))),
      new IrDeclare(L("fabs", IrType.F64), new IrIntrinsic(IrIntrinsicOp.Abs, IrType.F64, [R(x)])),
      new IrDeclare(L("uabs", IrType.U32), new IrIntrinsic(IrIntrinsicOp.Abs, IrType.U32, [R(u32)])),
      new IrDeclare(L("fmin", IrType.F64), new IrIntrinsic(IrIntrinsicOp.Min, IrType.F64, [R(x), R(y)])),
      new IrDeclare(L("fmax", IrType.F64), new IrIntrinsic(IrIntrinsicOp.Max, IrType.F64, [R(x), R(y)])),
      new IrDeclare(L("imax", IrType.I32), new IrIntrinsic(IrIntrinsicOp.Max, IrType.I32, [R(i32), I(5)])),
      new IrDeclare(L("fsign", IrType.I32), new IrIntrinsic(IrIntrinsicOp.Sign, IrType.I32, [R(x)])),
      new IrDeclare(L("isign", IrType.I32), new IrIntrinsic(IrIntrinsicOp.Sign, IrType.I32, [R(i32)])),
      new IrDeclare(L("fsqrt", IrType.F64), new IrIntrinsic(IrIntrinsicOp.Sqrt, IrType.F64, [R(x)])),
      new IrDeclare(L("ffloor", IrType.F64), new IrIntrinsic(IrIntrinsicOp.Floor, IrType.F64, [R(x)])),
      new IrDeclare(L("fceil", IrType.F64), new IrIntrinsic(IrIntrinsicOp.Ceiling, IrType.F64, [R(x)])),
      new IrDeclare(L("ftrunc", IrType.F64), new IrIntrinsic(IrIntrinsicOp.Truncate, IrType.F64, [R(x)])),
      new IrDeclare(L("freven", IrType.F64), new IrIntrinsic(IrIntrinsicOp.RoundEven, IrType.F64, [R(x)])),
      new IrDeclare(L("fraway", IrType.F64), new IrIntrinsic(IrIntrinsicOp.RoundAway, IrType.F64, [R(x)])),
      new IrDeclare(L("fb32", IrType.I32), new IrIntrinsic(IrIntrinsicOp.FloatBits, IrType.I32, [R(f32)])),
      new IrDeclare(L("fb64", IrType.I64), new IrIntrinsic(IrIntrinsicOp.FloatBits, IrType.I64, [R(x)])),
      new IrDeclare(L("bf32", IrType.F32), new IrIntrinsic(IrIntrinsicOp.BitsFloat, IrType.F32, [R(i32)])),
      new IrDeclare(L("bf64", IrType.F64), new IrIntrinsic(IrIntrinsicOp.BitsFloat, IrType.F64, [R(i64)])),
      new IrDeclare(L("isnan", IrType.Bool), new IrIntrinsic(IrIntrinsicOp.IsNaN, IrType.Bool, [R(x)])),
      new IrDeclare(L("isinf", IrType.Bool), new IrIntrinsic(IrIntrinsicOp.IsInfinity, IrType.Bool, [R(x)])),
      new IrDeclare(L("isfin", IrType.Bool), new IrIntrinsic(IrIntrinsicOp.IsFinite, IrType.Bool, [R(x)])),
      new IrDeclare(L("isneg", IrType.Bool), new IrIntrinsic(IrIntrinsicOp.IsNegative, IrType.Bool, [R(x)])),
      new IrDeclare(L("isnorm", IrType.Bool), new IrIntrinsic(IrIntrinsicOp.IsNormal, IrType.Bool, [R(x)])),
      new IrDeclare(L("issub", IrType.Bool), new IrIntrinsic(IrIntrinsicOp.IsSubnormal, IrType.Bool, [R(x)])),
      new IrDeclare(L("isint", IrType.Bool), new IrIntrinsic(IrIntrinsicOp.IsInteger, IrType.Bool, [R(x)])),
      new IrDeclare(L("csign", IrType.F64), new IrIntrinsic(IrIntrinsicOp.CopySign, IrType.F64, [R(x), R(y)])),
      new IrDeclare(L("hw", IrType.U32), new IrIntrinsic(IrIntrinsicOp.HighWord, IrType.U32, [R(i64)])),
      new IrDeclare(L("jw", IrType.I64), new IrIntrinsic(IrIntrinsicOp.JoinWords, IrType.I64, [R(u32), R(u32)])),
      new IrDeclare(L("u2i", IrType.I64), new IrConvert(IrType.I64, R(u64))),
      new IrDeclare(L("i2u", IrType.U64), new IrConvert(IrType.U64, R(i64))),
      new IrDeclare(L("i2f", IrType.F64), new IrConvert(IrType.F64, R(i32))),
      new IrDeclare(L("i2f32", IrType.F32), new IrConvert(IrType.F32, R(i32))),
      new IrDeclare(L("f2u64", IrType.U64), new IrConvert(IrType.U64, R(x), IrConversion.Saturate)),
      new IrDeclare(L("c_trap", IrType.I8), new IrConvert(IrType.I8, R(i32), IrConversion.Trap)),
      new IrDeclare(L("shrl", IrType.I64), Bin(IrBinaryOp.ShiftRightLogical, R(i64), I(4))),
      new IrDeclare(L("shlu64", IrType.U64), Bin(IrBinaryOp.ShiftLeft, R(u64), I(3))),
      new IrDeclare(L("u64chk", IrType.U64), Bin(IrBinaryOp.AddChecked, R(u64), C(IrType.U64, 1))),
      new IrDeclare(L("bnot", IrType.I32), new IrUnary(IrUnaryOp.BitNot, R(i32))),
      new IrDeclare(L("land", IrType.Bool), Bin(IrBinaryOp.LogicalAnd, B(true), B(false))),
      new IrDeclare(L("lor", IrType.Bool), Bin(IrBinaryOp.LogicalOr, B(false), B(true))),
      new IrDeclare(fvar, F64(1.0)),
      new IrExprStmt(new IrStep(R(fvar), true, true, false)),
      new IrExprStmt(new IrStep(new IrArrayLoad(R(arr), I(0)), true, true, true)),
      new IrExprStmt(new IrStep(new IrArrayLoad(R(arr), I(0)), false, false, false)),
      new IrReturn(R(i32)));

    var src = Emit(new IrModule("AllScalars", [fn]));
    Assert.That(src, Does.Contain("POSIX::copysign"));
    Assert.That(src, Does.Contain("POSIX::isnan"));
    Assert.That(src, Does.Contain("POSIX::isnormal"));
    Assert.That(src, Does.Contain("unpack('q', pack('Q'"));

    // Legalize integer library intrinsics (BigMul, DivFloor, ByteSwap, etc.)
    var libFn = Function("lib_int", IrType.U64, [u32],
      new IrDeclare(L("bm", IrType.U64), new IrIntrinsic(IrIntrinsicOp.BigMul, IrType.U64, [R(u32), R(u32)])),
      new IrDeclare(L("bs", IrType.U32), new IrIntrinsic(IrIntrinsicOp.ByteSwap, IrType.U32, [R(u32)])),
      new IrReturn(R(L("bm", IrType.U64))));
    var libSrc = Legalized(new IrModule("LibInt", [libFn]));
    Assert.That(libSrc, Does.Contain("sub lib_int"));
  }

  [Test]
  public void AllMemoryViewsPointersStringsEnumsCollectionsLambdasAndControlBranches() {
    var color = new IrEnumType("Shade", IrType.I32, [new("Dark", 1), new("Light", 2)]) { IsPublic = true };
    var listType = IrType.List(IrType.String);
    var mapType = IrType.Map(IrType.String, IrType.I32, MapOrder.Insertion);
    var setType = IrType.Set(IrType.I32, MapOrder.Insertion);
    var spanType = IrType.Span(IrType.I32);
    var ptrType = IrType.Pointer(IrType.I32);
    var pt = new IrStructType("Pt");
    pt.Define([new("X", IrType.I32, false)]);

    var arr = L("arr", IrType.Array(IrType.I32));
    var s = L("s", IrType.String);
    var u16a = L("u16a", IrType.Array(IrType.U16));
    var sh = L("sh", IrType.Enum(color));
    var opt = L("opt", IrType.Optional(IrType.I32));
    var list = L("list", listType);
    var map = L("map", mapType);
    var set = L("set", setType);
    var v1 = L("v1", spanType);
    var v2 = L("v2", spanType);
    var p1 = L("p1", ptrType);
    var p2 = L("p2", ptrType);
    var loc = L("loc", IrType.I32);
    var x = L("x", IrType.I32);
    var lam = L("lam", IrType.Function(IrType.I32, IrType.I32));
    var vlam = L("vlam", IrType.Function(IrType.Void));

    var helperFn = Function("H", false, IrType.I32, [L("z", IrType.I32)], new IrReturn(R(L("z", IrType.I32))));
    var fn = Function("everything", IrType.I32, [arr, s, u16a, sh, opt],
      new IrDeclare(loc, null),
      new IrBlock([new IrAssign(loc, I(1))]),
      new IrDeclare(L("sarr", IrType.Array(IrType.Struct(pt))), new IrNewArray(IrType.Array(IrType.Struct(pt)), I(2))),
      new IrDeclare(v1, new IrIntrinsic(IrIntrinsicOp.ViewOf, spanType, [R(arr), I(0)])),
      new IrDeclare(v2, new IrIntrinsic(IrIntrinsicOp.ViewOf, spanType, [R(arr), I(0), I(1)])),
      new IrDeclare(L("sl1", IrType.Span(IrType.I32)), new IrIntrinsic(IrIntrinsicOp.Slice, spanType, [R(v1), I(0)])),
      new IrDeclare(L("sa", spanType), new IrIntrinsic(IrIntrinsicOp.StackAlloc, spanType, [I(2)])),
      new IrDeclare(L("tc", IrType.Bool), new IrIntrinsic(IrIntrinsicOp.ViewTryCopy, IrType.Bool, [R(v1), R(v2)])),
      new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ViewFill, IrType.Void, [R(v2), I(3)])),
      new IrDeclare(L("va", IrType.Array(IrType.I32)), new IrIntrinsic(IrIntrinsicOp.ViewToArray, IrType.Array(IrType.I32), [R(v2)])),
      new IrDeclare(p1, new IrIntrinsic(IrIntrinsicOp.Pin, ptrType, [R(arr), I(0)])),
      new IrDeclare(p2, new IrIntrinsic(IrIntrinsicOp.AddressOf, ptrType, [R(loc)])),
      new IrDeclare(L("pa", ptrType), new IrIntrinsic(IrIntrinsicOp.PointerAdd, ptrType, [R(p1), I(1)])),
      new IrDeclare(L("pd", IrType.I64), new IrIntrinsic(IrIntrinsicOp.PointerDifference, IrType.I64, [R(p1), R(p2)])),
      new IrDeclare(L("peq", IrType.Bool), new IrIntrinsic(IrIntrinsicOp.PointerEqual, IrType.Bool, [R(p1), R(p2)])),
      new IrDeclare(L("pne", IrType.Bool), new IrIntrinsic(IrIntrinsicOp.PointerNotEqual, IrType.Bool, [R(p1), R(p2)])),
      new IrDeclare(L("plt", IrType.Bool), new IrIntrinsic(IrIntrinsicOp.PointerLess, IrType.Bool, [R(p1), R(p2)])),
      new IrDeclare(L("ple", IrType.Bool), new IrIntrinsic(IrIntrinsicOp.PointerLessOrEqual, IrType.Bool, [R(p1), R(p2)])),
      new IrDeclare(L("pgt", IrType.Bool), new IrIntrinsic(IrIntrinsicOp.PointerGreater, IrType.Bool, [R(p1), R(p2)])),
      new IrDeclare(L("pge", IrType.Bool), new IrIntrinsic(IrIntrinsicOp.PointerGreaterOrEqual, IrType.Bool, [R(p1), R(p2)])),
      new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.PointerStore, IrType.Void, [R(p1), I(4)])),
      new IrDeclare(L("vp", IrType.Pointer(IrType.Void)), new IrIntrinsic(IrIntrinsicOp.PointerCast, IrType.Pointer(IrType.Void), [R(p1)])),
      // All string intrinsics
      new IrDeclare(L("cu", IrType.U16), new IrIntrinsic(IrIntrinsicOp.StrCodeUnitAt, IrType.U16, [R(s), I(0)])),
      new IrDeclare(L("fcu", IrType.String), new IrIntrinsic(IrIntrinsicOp.StrFromCodeUnit, IrType.String, [C(IrType.U16, 65)])),
      new IrDeclare(L("fcus1", IrType.String), new IrIntrinsic(IrIntrinsicOp.StrFromCodeUnits, IrType.String, [R(u16a)])),
      new IrDeclare(L("fcus3", IrType.String), new IrIntrinsic(IrIntrinsicOp.StrFromCodeUnits, IrType.String, [R(u16a), I(0), I(1)])),
      new IrDeclare(L("tcu", IrType.Array(IrType.U16)), new IrIntrinsic(IrIntrinsicOp.StrToCodeUnits, IrType.Array(IrType.U16), [R(s)])),
      new IrDeclare(L("rep1", IrType.String), new IrIntrinsic(IrIntrinsicOp.StrRepeat, IrType.String, [R(s), I(2)])),
      new IrDeclare(L("rep2", IrType.String), new IrIntrinsic(IrIntrinsicOp.StrRepeat, IrType.String, [C(IrType.U16, 65), I(2)])),
      new IrDeclare(L("sub2", IrType.String), new IrIntrinsic(IrIntrinsicOp.StrSubstring, IrType.String, [R(s), I(1)])),
      new IrDeclare(L("seq", IrType.Bool), new IrIntrinsic(IrIntrinsicOp.StrEquals, IrType.Bool, [R(s), R(s)])),
      new IrDeclare(L("scmp", IrType.I32), new IrIntrinsic(IrIntrinsicOp.StrCompareOrdinal, IrType.I32, [R(s), R(s)])),
      new IrDeclare(L("idx3", IrType.I32), new IrIntrinsic(IrIntrinsicOp.StrIndexOf, IrType.I32, [R(s), C(IrType.U16, 65), I(0)])),
      new IrDeclare(L("lidx2", IrType.I32), new IrIntrinsic(IrIntrinsicOp.StrLastIndexOf, IrType.I32, [R(s), new IrStrConst("a")])),
      new IrDeclare(L("lidx3", IrType.I32), new IrIntrinsic(IrIntrinsicOp.StrLastIndexOf, IrType.I32, [R(s), C(IrType.U16, 65), I(0)])),
      new IrDeclare(L("sw", IrType.Bool), new IrIntrinsic(IrIntrinsicOp.StrStartsWith, IrType.Bool, [R(s), C(IrType.U16, 65)])),
      new IrDeclare(L("ew", IrType.Bool), new IrIntrinsic(IrIntrinsicOp.StrEndsWith, IrType.Bool, [R(s), new IrStrConst("z")])),
      new IrDeclare(L("rf", IrType.String), new IrIntrinsic(IrIntrinsicOp.StrReplaceFirst, IrType.String, [R(s), new IrStrConst("a"), new IrStrConst("b")])),
      new IrDeclare(L("trs", IrType.String), new IrIntrinsic(IrIntrinsicOp.StrTrimStart, IrType.String, [R(s), I(0)])),
      new IrDeclare(L("tre", IrType.String), new IrIntrinsic(IrIntrinsicOp.StrTrimEnd, IrType.String, [R(s), I(0)])),
      new IrDeclare(L("pe", IrType.String), new IrIntrinsic(IrIntrinsicOp.StrPadEnd, IrType.String, [R(s), I(5), new IrStrConst("0")])),
      // Enums & enum switch
      new IrDeclare(L("ehf", IrType.Bool), new IrIntrinsic(IrIntrinsicOp.EnumHasFlag, IrType.Bool, [R(sh), R(sh)])),
      new IrDeclare(L("eid", IrType.Bool), new IrIntrinsic(IrIntrinsicOp.EnumIsDefined, IrType.Bool, [R(sh)])),
      new IrDeclare(L("en", IrType.Array(IrType.String)), new IrIntrinsic(IrIntrinsicOp.EnumNames, IrType.Array(IrType.String), [R(sh)])),
      new IrDeclare(L("ev", IrType.Array(IrType.Enum(color))), new IrIntrinsic(IrIntrinsicOp.EnumValues, IrType.Array(IrType.Enum(color)), [R(sh)])),
      new IrDeclare(L("ets", IrType.String), new IrIntrinsic(IrIntrinsicOp.EnumToString, IrType.String, [R(sh)])),
      new IrDeclare(L("ep", IrType.Enum(color)), new IrIntrinsic(IrIntrinsicOp.EnumParse, IrType.Enum(color), [new IrStrConst("Dark"), B(false)])),
      new IrSwitch(R(sh), [
        new IrSwitchSection([1], false, Block(new IrAssign(loc, I(10)))),
        new IrSwitchSection([], true, Block(new IrAssign(loc, I(20))))
      ]),
      // Optionals
      new IrDeclare(L("uw1", IrType.I32), new IrUnwrap(R(opt))),
      new IrDeclare(L("uw2", IrType.I32), new IrUnwrap(R(opt)) { IsTested = true }),
      // Collections
      new IrDeclare(list, new IrCollection(IrCollectionOp.ListOf, listType, [new IrStrConst("a")])),
      new IrDeclare(L("lfa", listType), new IrCollection(IrCollectionOp.ListFromArray, listType, [new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.String), [new IrStrConst("b")])])),
      new IrDeclare(L("li", IrType.I32), new IrCollection(IrCollectionOp.ListIndexOfEquals, IrType.I32, [R(list), new IrStrConst("a")])),
      new IrForEachCollection(R(list), EnumerationMode.Checked, L("le", IrType.String), null, Block(new IrAssign(loc, I(2)))),
      new IrDeclare(map, new IrCollection(IrCollectionOp.MapNew, mapType, [])),
      new IrExprStmt(new IrCollection(IrCollectionOp.MapAdd, IrType.Void, [R(map), new IrStrConst("a"), I(1)])),
      new IrDeclare(L("mta", IrType.Bool), new IrCollection(IrCollectionOp.MapTryAdd, IrType.Bool, [R(map), new IrStrConst("b"), I(2)])),
      new IrDeclare(L("mtg", IrType.Optional(IrType.I32)), new IrCollection(IrCollectionOp.MapTryGet, IrType.Optional(IrType.I32), [R(map), new IrStrConst("a")])),
      new IrDeclare(set, new IrCollection(IrCollectionOp.SetNew, setType, [])),
      new IrForEachCollection(R(set), EnumerationMode.Checked, L("se", IrType.I32), null, Block(new IrAssign(loc, R(L("se", IrType.I32))))),
      // Lambdas & FunctionRef
      new IrDeclare(lam, IrLambda.Of([x], IrType.I32, [], Block(new IrReturn(R(x))))),
      new IrDeclare(vlam, IrLambda.Of([], IrType.Void, [], Block())),
      new IrDeclare(L("fref", IrType.Function(IrType.I32, IrType.I32)), new IrFunctionRef(IrType.Function(IrType.I32, IrType.I32), "H")),
      new IrDeclare(L("lres", IrType.I32), new IrInvoke(IrType.I32, R(lam), [I(5)])),
      new IrExprStmt(new IrInvoke(IrType.Void, R(vlam), [])),
      // Try/catch/finally with break/continue/rethrow/nested try/message read
      new IrWhile(B(true), Block(
        new IrTry(
          Block(
            new IrIf(Bin(IrBinaryOp.Equal, R(loc), I(1)), Block(new IrContinue()), null),
            new IrIf(Bin(IrBinaryOp.Equal, R(loc), I(2)), Block(new IrBreak()), null),
            new IrTry(Block(new IrReturn(I(9))), [], Block(new IrAssign(loc, I(3))))),
          [
            new IrCatch(IrExceptionClass.Exception, L("ex", IrType.Object(IrExceptionClass.Exception)), null,
              Block(new IrDeclare(L("msg", IrType.String), new IrFieldLoad(R(L("ex", IrType.Object(IrExceptionClass.Exception))), IrExceptionClass.Message)),
                new IrRethrow()))
          ],
          Block(new IrAssign(loc, I(4)))))),
      new IrReturn(R(loc)));

    var mod = new IrModule("Everything", [helperFn, fn]) { Types = [color, pt] };
    var src = Emit(mod);
    Assert.That(src, Does.Contain("_ply_view_slice"));
    Assert.That(src, Does.Contain("_ply_str_pad"));
    Assert.That(src, Does.Contain("_trap_message"));
    Assert.That(src, Does.Contain("die $_e;"));
    Assert.That(src, Does.Contain("\\&_h"));

    // Invalid IR verification failure in Check and Emit
    var badIr = new IrModule("BadIr", [Function("f", IrType.I32, [], new IrReturn(new IrStrConst("wrong")))]);
    Assert.That(Problems(badIr), Is.Not.Empty);
    Assert.That(() => Emit(badIr), Throws.ArgumentException);
  }

  [Test]
  public void RemainingBackendBranches_EndianAllTypes_Loops_Switches_Try_Defaults_AndRefusals() {
    // PerlNaming empty segment after stripping punctuation
    Assert.That(PerlNaming.Package("foo_!!!_bar"), Is.EqualTo("FooBar"));

    // All endian load/store types (i16/u16/i32/u32/i64/u64/f32/f64, little & big) + string bytes ArrayOf/ArrayClear
    var buf = L("buf", IrType.Array(IrType.U8));
    var endianStmts = new List<IrStmt> {
      new IrDeclare(L("ba", IrType.Array(IrType.U8)), new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.U8), [C(IrType.U8, 1), C(IrType.U8, 2)])),
      new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayClear, IrType.Void, [R(buf)]))
    };
    var idx = 0;
    foreach (var t in new[] { IrType.I16, IrType.U16, IrType.I32, IrType.U32, IrType.I64, IrType.U64, IrType.F32, IrType.F64 }) {
      var ldL = L($"ll{idx}", t);
      var ldB = L($"lb{idx}", t);
      endianStmts.Add(new IrDeclare(ldL, new IrIntrinsic(IrIntrinsicOp.LoadLittle, t, [R(buf), I(0)])));
      endianStmts.Add(new IrDeclare(ldB, new IrIntrinsic(IrIntrinsicOp.LoadBig, t, [R(buf), I(0)])));
      endianStmts.Add(new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.StoreLittle, IrType.Void, [R(buf), I(0), R(ldL)])));
      endianStmts.Add(new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.StoreBig, IrType.Void, [R(buf), I(0), R(ldB)])));
      ++idx;
    }
    endianStmts.Add(new IrReturn(null));
    var endianFn = Function("endian_all", IrType.Void, [buf], [.. endianStmts]);
    Emit(new IrModule("EndianArr", [endianFn]), PerlProfile.Default);
    Emit(new IrModule("EndianStr", [endianFn]), PerlProfile.Default with { ByteStorage = PerlByteStorage.String });

    // Comparisons in expression position, string intrinsics U16/String variants, DefaultValue for all kinds, void return omission,
    // TryEmitCountedLoop fallbacks, GeneralFor with continue, Switch default-only & no-default, multi-catch & void try-return
    var stWithStr = new IrStructType("Labelled");
    stWithStr.Define([new("Code", IrType.I32, false), new("Score", IrType.F64, false)]);
    var customEx = new IrClassType("MyCustomEx");
    customEx.Inherit(IrExceptionClass.Exception, []);
    customEx.Define([]);
    var customCtor = new IrConstructor("MyCustomEx__ctor");
    customEx.DefineMembers([customCtor], [], [], []);
    var customSelf = L("self", IrType.Object(customEx));
    var customMsg = L("m", IrType.String);
    var customCtorFn = Function("MyCustomEx__ctor", false, IrType.Void, [customSelf, customMsg],
      new IrExprStmt(new IrCall(IrExceptionClass.Constructor(IrExceptionClass.Exception, 1).Function, IrType.Void, [R(customSelf), R(customMsg)])),
      new IrReturn(null));

    var a = L("a", IrType.I32);
    var b = L("b", IrType.I32);
    var s = L("s", IrType.String);
    var i64c = L("i64c", IrType.I64);
    var iv1 = L("iv1", IrType.I32);
    var iv2 = L("iv2", IrType.I32);
    var iv3 = L("iv3", IrType.I32);
    var iv4 = L("iv4", IrType.I32);
    var iv5 = L("iv5", IrType.I32);
    var arr = L("arr", IrType.Array(IrType.I32));
    var lst = L("lst", IrType.List(IrType.I32));
    var exVar = L("exVar", IrType.Object(IrExceptionClass.Exception));

    var branchFn = Function("branches", IrType.Void, [a, b, s, arr],
      // Default values (optional, list, map, set, object)
      new IrDeclare(L("dopt", IrType.Optional(IrType.I32)), null),
      new IrDeclare(L("dlst", IrType.List(IrType.I32)), null),
      new IrDeclare(L("dmap", IrType.Map(IrType.String, IrType.I32, MapOrder.Insertion)), null),
      new IrDeclare(L("dset", IrType.Set(IrType.I32, MapOrder.Insertion)), null),
      new IrDeclare(L("dobj", IrType.Object(IrExceptionClass.Exception)), null),
      // All 6 comparisons as expressions
      new IrDeclare(L("c1", IrType.Bool), Bin(IrBinaryOp.Equal, R(a), R(b))),
      new IrDeclare(L("c2", IrType.Bool), Bin(IrBinaryOp.NotEqual, R(a), R(b))),
      new IrDeclare(L("c3", IrType.Bool), Bin(IrBinaryOp.Less, R(a), R(b))),
      new IrDeclare(L("c4", IrType.Bool), Bin(IrBinaryOp.LessOrEqual, R(a), R(b))),
      new IrDeclare(L("c5", IrType.Bool), Bin(IrBinaryOp.Greater, R(a), R(b))),
      new IrDeclare(L("c6", IrType.Bool), Bin(IrBinaryOp.GreaterOrEqual, R(a), R(b))),
      // Struct with string field equality
      new IrDeclare(L("se", IrType.Bool), new IrStructEquals(new IrStructDefault(IrType.Struct(stWithStr)), new IrStructDefault(IrType.Struct(stWithStr)))),
      // String U16/String branches
      new IrDeclare(L("sws", IrType.Bool), new IrIntrinsic(IrIntrinsicOp.StrStartsWith, IrType.Bool, [R(s), new IrStrConst("a")])),
      new IrDeclare(L("ewc", IrType.Bool), new IrIntrinsic(IrIntrinsicOp.StrEndsWith, IrType.Bool, [R(s), C(IrType.U16, 65)])),
      new IrDeclare(L("cnc", IrType.Bool), new IrIntrinsic(IrIntrinsicOp.StrContains, IrType.Bool, [R(s), C(IrType.U16, 65)])),
      new IrDeclare(L("rac", IrType.String), new IrIntrinsic(IrIntrinsicOp.StrReplaceAll, IrType.String, [R(s), C(IrType.U16, 65), C(IrType.U16, 66)])),
      new IrDeclare(L("sp2", IrType.Array(IrType.String)), new IrIntrinsic(IrIntrinsicOp.StrSplit, IrType.Array(IrType.String), [R(s), C(IrType.U16, 44), I(-1)])),
      new IrDeclare(L("sp3", IrType.Array(IrType.String)), new IrIntrinsic(IrIntrinsicOp.StrSplit, IrType.Array(IrType.String), [R(s), new IrStrConst(","), I(2)])),
      // ListRemoveLast
      new IrDeclare(lst, new IrCollection(IrCollectionOp.ListNew, IrType.List(IrType.I32), [])),
      new IrDeclare(L("popped", IrType.Optional(IrType.I32)), new IrCollection(IrCollectionOp.ListRemoveLast, IrType.Optional(IrType.I32), [R(lst)])),
      // Void return modifier
      new IrIf(Bin(IrBinaryOp.Equal, R(a), I(99)), Block(new IrReturn(null)), null),
      // Counted loop fallbacks: i64 counter, non-iv condition, non-cmp condition, modified iv, general for with continue, reverse with > and IrStep dec
      new IrDeclare(i64c, C(IrType.I64, 0)),
      new IrFor(Bin(IrBinaryOp.Less, R(i64c), C(IrType.I64, 5)), Block(new IrIf(Bin(IrBinaryOp.Equal, R(i64c), C(IrType.I64, 2)), Block(new IrContinue()), null)),
        Block(new IrAssign(i64c, Bin(IrBinaryOp.Add, R(i64c), C(IrType.I64, 1))))),
      new IrDeclare(iv1, I(0)),
      new IrFor(Bin(IrBinaryOp.Less, R(a), I(5)), Block(new IrAssign(a, Bin(IrBinaryOp.Add, R(a), I(1)))), Block(new IrExprStmt(new IrStep(R(iv1), true, false, false)))),
      new IrDeclare(iv2, I(0)),
      new IrFor(Bin(IrBinaryOp.Equal, R(iv2), I(0)), Block(new IrAssign(iv2, I(1))), Block(new IrExprStmt(new IrStep(R(iv2), true, false, false)))),
      new IrDeclare(iv3, I(0)),
      new IrFor(Bin(IrBinaryOp.Less, R(iv3), I(5)), Block(new IrAssign(iv3, Bin(IrBinaryOp.Add, R(iv3), I(2)))), Block(new IrExprStmt(new IrStep(R(iv3), true, false, false)))),
      new IrDeclare(iv4, I(5)),
      new IrFor(Bin(IrBinaryOp.Greater, R(iv4), I(0)), Block(new IrAssign(b, R(iv4))), Block(new IrExprStmt(new IrStep(R(iv4), false, false, false)))),
      new IrDeclare(iv5, I(0)),
      new IrFor(Bin(IrBinaryOp.Less, R(iv5), new IrArrayLength(R(arr))),
        Block(
          new IrDeclare(L("el", IrType.I32), new IrArrayLoad(R(arr), R(iv5))),
          new IrAssign(b, Bin(IrBinaryOp.Add, R(b), R(iv5))),
          new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayFill, IrType.Void, [R(arr), I(0)]))),
        Block(new IrExprStmt(new IrStep(R(iv5), true, false, false)))),
      // While with continue inside bare block (not statement modifier)
      new IrWhile(Bin(IrBinaryOp.Greater, R(b), I(0)), Block(
        new IrAssign(b, Bin(IrBinaryOp.Subtract, R(b), I(1))),
        new IrContinue())),
      // Switch: default-only, and no-default
      new IrSwitch(R(a), [new IrSwitchSection([], true, Block(new IrAssign(b, I(1))))]),
      new IrSwitch(R(a), [new IrSwitchSection([1], false, Block(new IrAssign(b, I(2))))]),
      // Try with multiple catches, default-ctor exception, custom exception, IrThrowExpression, non-IrNew throw, and void return + do-while continue
      new IrDoWhile(Block(
        new IrTry(
          Block(
            new IrIf(Bin(IrBinaryOp.Equal, R(a), I(1)), Block(new IrThrow(new IrNew(IrType.Object(IrExceptionClass.DivideByZeroException), IrExceptionClass.Constructor(IrExceptionClass.DivideByZeroException, 0), []))), null),
            new IrIf(Bin(IrBinaryOp.Equal, R(a), I(2)), Block(new IrThrow(new IrNew(IrType.Object(customEx), customCtor, [new IrStrConst("c")]))), null),
            new IrIf(Bin(IrBinaryOp.Equal, R(a), I(3)), Block(new IrDeclare(exVar, new IrNew(IrType.Object(IrExceptionClass.OverflowException), IrExceptionClass.Constructor(IrExceptionClass.OverflowException, 1), [new IrStrConst("ov")])), new IrThrow(R(exVar))), null),
            new IrIf(Bin(IrBinaryOp.Equal, R(a), I(4)), Block(new IrDeclare(L("te", IrType.I32), new IrThrowExpression(IrType.I32, new IrNew(IrType.Object(IrExceptionClass.InvalidOperationException), IrExceptionClass.Constructor(IrExceptionClass.InvalidOperationException, 0), [])))), null),
            new IrIf(Bin(IrBinaryOp.Equal, R(a), I(5)), Block(new IrContinue()), null),
            new IrReturn(null)),
          [
            new IrCatch(IrExceptionClass.DivideByZeroException, null, null, Block(new IrAssign(b, I(10)))),
            new IrCatch(IrExceptionClass.OverflowException, L("ovex", IrType.Object(IrExceptionClass.OverflowException)), null, Block(new IrAssign(b, I(20)))),
            new IrCatch(IrExceptionClass.Exception, null, null, Block(new IrAssign(b, I(30))))
          ],
          null)), Bin(IrBinaryOp.Less, R(b), I(3))));

    var mod = new IrModule("Branches", [customCtorFn, branchFn]) { Types = [stWithStr, customEx] };
    var emitted = Emit(mod);
    Assert.That(emitted, Does.Contain("return;\n}"));
    Assert.That(emitted, Does.Contain("elsif (_trap_matches"));

    // Globals with default initializers (Optional, List, Map, Set, Object) + mixed float/int function + IsIntegerOnlyFunction LoadLittle
    // + IsArrayWritten (Store, ArrayCopy, ArrayClear) + IsLocalModified (IrStep, IrBind) + TryEmitCountedLoop mismatch + AddressOf refusal
    var gOpt = new IrGlobal("GOpt", IrType.Optional(IrType.I32), null);
    var gLst = new IrGlobal("GLst", IrType.List(IrType.I32), null);
    var gMap = new IrGlobal("GMap", IrType.Map(IrType.String, IrType.I32, MapOrder.Insertion), null);
    var gSet = new IrGlobal("GSet", IrType.Set(IrType.I32, MapOrder.Insertion), null);
    var gObj = new IrGlobal("GObj", IrType.Object(IrExceptionClass.Exception), null);
    var fd = L("fd", IrType.F64);
    var mi = L("mi", IrType.I32);
    var ml = L("ml", IrType.I64);
    var mu = L("mu", IrType.U32);
    var mul = L("mul", IrType.U64);
    var ibuf = L("ibuf", IrType.Array(IrType.U8));
    var arr2 = L("arr2", IrType.Array(IrType.I32));
    var k1 = L("k1", IrType.I32);
    var k2 = L("k2", IrType.I32);
    var k3 = L("k3", IrType.I32);
    var k4 = L("k4", IrType.I32);
    var k5 = L("k5", IrType.I32);
    var k6 = L("k6", IrType.I32);

    var mixedFn = Function("mixed_ops", IrType.I32, [fd, mi, ml, mu, mul, arr2],
      new IrDeclare(L("ladd", IrType.I64), Bin(IrBinaryOp.Add, R(ml), C(IrType.I64, 1))),
      new IrDeclare(L("band", IrType.I32), Bin(IrBinaryOp.And, R(mi), I(7))),
      new IrDeclare(L("idiv", IrType.I32), Bin(IrBinaryOp.Divide, R(mi), I(2))),
      new IrDeclare(L("ishl", IrType.I32), Bin(IrBinaryOp.ShiftLeft, R(mi), I(2))),
      new IrDeclare(L("ishr", IrType.I32), Bin(IrBinaryOp.ShiftRight, R(mi), I(1))),
      new IrDeclare(L("lshr", IrType.I32), Bin(IrBinaryOp.ShiftRightLogical, R(mi), I(1))),
      new IrDeclare(L("ushr", IrType.U32), Bin(IrBinaryOp.ShiftRight, R(mu), I(1))),
      new IrDeclare(L("u64shr", IrType.U64), Bin(IrBinaryOp.ShiftRight, R(mul), I(1))),
      new IrDeclare(L("i64shr", IrType.I64), Bin(IrBinaryOp.ShiftRightLogical, R(ml), I(1))),
      new IrDeclare(L("sdec", IrType.I32), new IrStep(R(mi), false, false, true)),
      new IrDeclare(L("idconv", IrType.I32), new IrConvert(IrType.I32, R(mi))),
      // Mismatched direction: +1 step with > condition
      new IrDeclare(k1, I(0)),
      new IrFor(Bin(IrBinaryOp.Greater, R(k1), I(5)), Block(new IrAssign(mi, R(k1))), Block(new IrExprStmt(new IrStep(R(k1), true, false, false)))),
      // IsLocalModified via IrStep and IrBind
      new IrDeclare(k2, I(0)),
      new IrFor(Bin(IrBinaryOp.Less, R(k2), I(5)), Block(new IrExprStmt(new IrStep(R(k2), true, false, false))), Block(new IrExprStmt(new IrStep(R(k2), true, false, false)))),
      new IrDeclare(k3, I(0)),
      new IrFor(Bin(IrBinaryOp.Less, R(k3), I(5)), Block(new IrExprStmt(new IrBind(k3, I(9)))), Block(new IrExprStmt(new IrStep(R(k3), true, false, false)))),
      // IsArrayWritten via IrStore, ArrayCopy, ArrayClear + TryMatchReadOnlyElementLoop non-load first stmt
      new IrDeclare(k4, I(0)),
      new IrFor(Bin(IrBinaryOp.Less, R(k4), new IrArrayLength(R(arr2))), Block(new IrStore(R(arr2), R(k4), I(1))), Block(new IrExprStmt(new IrStep(R(k4), true, false, false)))),
      new IrDeclare(k5, I(0)),
      new IrFor(Bin(IrBinaryOp.Less, R(k5), new IrArrayLength(R(arr2))), Block(new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayCopy, IrType.Void, [R(arr2), I(0), R(arr2), I(0), I(0)]))), Block(new IrExprStmt(new IrStep(R(k5), true, false, false)))),
      new IrDeclare(k6, I(0)),
      new IrFor(Bin(IrBinaryOp.Less, R(k6), new IrArrayLength(R(arr2))), Block(new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayClear, IrType.Void, [R(arr2)]))), Block(new IrExprStmt(new IrStep(R(k6), true, false, false)))),
      // Nested while & switch inside do-while (ContainsDirectLoopJump skip branches)
      new IrDoWhile(Block(
        new IrWhile(B(false), Block(new IrBreak())),
        new IrSwitch(R(mi), [new IrSwitchSection([1], false, Block(new IrBreak()))])), B(false)),
      new IrReturn(R(mi)));

    var intEndianFn = Function("int_endian", IrType.U32, [ibuf],
      new IrReturn(new IrIntrinsic(IrIntrinsicOp.LoadLittle, IrType.U32, [R(ibuf), I(0)])));

    var mixedMod = new IrModule("Mixed", [mixedFn, intEndianFn]) {
      Globals = [gOpt, gLst, gMap, gSet, gObj]
    };
    Emit(mixedMod);

    // PerlLibrary Covers branches: float intrinsic, HighWord, LoadLittle
    var libCoversFn = Function("lib_covers", IrType.U32, [fd, ml, ibuf],
      new IrDeclare(L("fa", IrType.F64), new IrIntrinsic(IrIntrinsicOp.Abs, IrType.F64, [R(fd)])),
      new IrDeclare(L("hw2", IrType.U32), new IrIntrinsic(IrIntrinsicOp.HighWord, IrType.U32, [R(ml)])),
      new IrDeclare(L("ll", IrType.U32), new IrIntrinsic(IrIntrinsicOp.LoadLittle, IrType.U32, [R(ibuf), I(0)])),
      new IrReturn(Bin(IrBinaryOp.Add, R(L("hw2", IrType.U32)), R(L("ll", IrType.U32)))));
    Legalized(new IrModule("LibCovers", [libCoversFn]));
  }

}
