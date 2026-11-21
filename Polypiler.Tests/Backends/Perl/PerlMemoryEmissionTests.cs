using NUnit.Framework;
using Polypiler.Core.Backends.Perl;
using Polypiler.Core.IR;
using static Polypiler.Tests.Backends.Perl.PerlIr;

namespace Polypiler.Tests.Backends.Perl;

/// <summary>Tests for Perl memory emission (#432): arrays, byte storage modes, endian loads/stores, lists, views, pointers and strings.</summary>
[TestFixture]
public sealed class PerlMemoryEmissionTests {

  [Test]
  public void Arrays_AllOperations_AndByteStorageModes() {
    var n = L("n", IrType.I32);
    var data = L("data", IrType.Array(IrType.U8));
    var buf = L("buf", IrType.Array(IrType.U8));
    var ints = L("ints", IrType.Array(IrType.I32));
    var fn = Function("bytes_ops", IrType.I32, [data, n],
      new IrDeclare(buf, new IrNewArray(IrType.Array(IrType.U8), R(n))),
      new IrDeclare(ints, new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.I32), [I(1), I(2), I(3)])),
      new IrStore(R(buf), I(0), new IrArrayLoad(R(data), I(0))),
      new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayCopy, IrType.Void, [R(data), I(0), R(buf), I(0), I(1)])),
      new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayFill, IrType.Void, [R(buf), C(IrType.U8, 7)])),
      new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayClear, IrType.Void, [R(ints)])),
      new IrReturn(Bin(IrBinaryOp.Add, new IrArrayLength(R(data)), new IrArrayLength(R(ints)))));

    var mod = new IrModule("MemOps", [fn]);
    var arrayMode = Emit(mod, PerlProfile.Default);
    Assert.That(arrayMode, Does.Contain("ref($data) ne 'ARRAY'"));
    Assert.That(arrayMode, Does.Contain("_ply_array_copy"));

    var stringProfile = PerlProfile.Default with { ByteStorage = PerlByteStorage.String };
    var stringMode = Emit(mod, stringProfile);
    Assert.That(stringMode, Does.Contain("vec("));
    Assert.That(stringMode, Does.Contain("substr("));
    Assert.That(stringMode, Does.Contain("length(${"));
  }

  [Test]
  public void EndianLoadsAndStores_BothByteStorageModes() {
    var buf = L("buf", IrType.Array(IrType.U8));
    var a = L("a", IrType.U16);
    var b = L("b", IrType.U16);
    var c = L("c", IrType.U32);
    var d = L("d", IrType.U32);
    var e = L("e", IrType.U64);
    var f = L("f", IrType.F32);
    var g = L("g", IrType.F64);
    var fn = Function("endian", IrType.U64, [buf],
      new IrDeclare(a, new IrIntrinsic(IrIntrinsicOp.LoadLittle, IrType.U16, [R(buf), I(0)])),
      new IrDeclare(b, new IrIntrinsic(IrIntrinsicOp.LoadBig, IrType.U16, [R(buf), I(2)])),
      new IrDeclare(c, new IrIntrinsic(IrIntrinsicOp.LoadLittle, IrType.U32, [R(buf), I(4)])),
      new IrDeclare(d, new IrIntrinsic(IrIntrinsicOp.LoadBig, IrType.U32, [R(buf), I(8)])),
      new IrDeclare(e, new IrIntrinsic(IrIntrinsicOp.LoadLittle, IrType.U64, [R(buf), I(12)])),
      new IrDeclare(f, new IrIntrinsic(IrIntrinsicOp.LoadLittle, IrType.F32, [R(buf), I(20)])),
      new IrDeclare(g, new IrIntrinsic(IrIntrinsicOp.LoadBig, IrType.F64, [R(buf), I(24)])),
      new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.StoreLittle, IrType.Void, [R(buf), I(0), R(a)])),
      new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.StoreBig, IrType.Void, [R(buf), I(4), R(c)])),
      new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.StoreLittle, IrType.Void, [R(buf), I(12), R(e)])),
      new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.StoreLittle, IrType.Void, [R(buf), I(20), R(f)])),
      new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.StoreBig, IrType.Void, [R(buf), I(24), R(g)])),
      new IrReturn(R(e)));

    var mod = new IrModule("Endian", [fn]);
    var arrSrc = Emit(mod, PerlProfile.Default);
    Assert.That(arrSrc, Does.Contain("unpack('Q<'"));
    Assert.That(arrSrc, Does.Contain("unpack('f<'"));
    Assert.That(arrSrc, Does.Contain("unpack('d>'"));
    Assert.That(arrSrc, Does.Contain("pack('v'"));
    Assert.That(arrSrc, Does.Contain("pack('N'"));

    var strSrc = Emit(mod, PerlProfile.Default with { ByteStorage = PerlByteStorage.String });
    Assert.That(strSrc, Does.Contain("substr($$_b"));
    Assert.That(strSrc, Does.Contain("unpack('v'"));
    Assert.That(strSrc, Does.Contain("unpack('n'"));
    Assert.That(strSrc, Does.Contain("unpack('V'"));
    Assert.That(strSrc, Does.Contain("unpack('N'"));
  }

  [Test]
  public void Lists_Views_Pointers_Strings_AndUnsupportedOverlayOrPointerCast() {
    var listType = IrType.List(IrType.I32);
    var spanType = IrType.Span(IrType.I32);
    var list = L("list", listType);
    var arr = L("arr", IrType.Array(IrType.I32));
    var s = L("s", IrType.String);
    var v = L("v", spanType);
    var vs = L("vs", spanType);
    var sp = L("sp", IrType.Array(IrType.String));
    var vl = L("vl", IrType.I32);
    var sl = L("sl", IrType.I32);
    var p = L("p", IrType.Pointer(IrType.I32));

    var fn = Function("containers", IrType.I32, [arr, s],
      new IrDeclare(list, new IrCollection(IrCollectionOp.ListNew, listType, [])),
      new IrExprStmt(new IrCollection(IrCollectionOp.ListAdd, IrType.Void, [R(list), I(10)])),
      new IrExprStmt(new IrCollection(IrCollectionOp.ListSet, IrType.Void, [R(list), I(0), I(20)])),
      new IrExprStmt(new IrCollection(IrCollectionOp.ListInsert, IrType.Void, [R(list), I(0), I(5)])),
      new IrExprStmt(new IrCollection(IrCollectionOp.ListRemoveAt, IrType.Void, [R(list), I(1)])),
      new IrDeclare(L("cnt", IrType.I32), new IrCollection(IrCollectionOp.ListCount, IrType.I32, [R(list)])),
      new IrDeclare(L("first", IrType.I32), new IrCollection(IrCollectionOp.ListGet, IrType.I32, [R(list), I(0)])),
      new IrDeclare(L("has10", IrType.Bool), new IrCollection(IrCollectionOp.ListContains, IrType.Bool, [R(list), I(10)])),
      new IrDeclare(L("copy", IrType.Array(IrType.I32)), new IrCollection(IrCollectionOp.ListCopyToArray, IrType.Array(IrType.I32), [R(list)])),
      new IrExprStmt(new IrCollection(IrCollectionOp.ListClear, IrType.Void, [R(list)])),
      new IrDeclare(v, new IrIntrinsic(IrIntrinsicOp.ViewOf, spanType, [R(arr)])),
      new IrDeclare(vs, new IrIntrinsic(IrIntrinsicOp.Slice, spanType, [R(v), I(0), I(1)])),
      new IrStore(R(vs), I(0), I(7)),
      new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ViewCopy, IrType.Void, [R(v), R(vs)])),
      new IrDeclare(L("veq", IrType.Bool), new IrIntrinsic(IrIntrinsicOp.ViewEqual, IrType.Bool, [R(v), R(vs)])),
      new IrDeclare(vl, new IrIntrinsic(IrIntrinsicOp.ViewLength, IrType.I32, [R(vs)])),
      new IrDeclare(L("vg", IrType.I32), new IrArrayLoad(R(vs), I(0))),
      new IrDeclare(p, new IrIntrinsic(IrIntrinsicOp.Pin, IrType.Pointer(IrType.I32), [R(arr)])),
      new IrDeclare(L("pv", IrType.I32), new IrIntrinsic(IrIntrinsicOp.PointerLoad, IrType.I32, [R(p)])),
      new IrDeclare(sl, new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [R(s)])),
      new IrDeclare(L("sc", IrType.String), new IrIntrinsic(IrIntrinsicOp.StrConcat, IrType.String, [R(s), new IrStrConst("!")])),
      new IrDeclare(L("sub", IrType.String), new IrIntrinsic(IrIntrinsicOp.StrSubstring, IrType.String, [R(s), I(0), I(1)])),
      new IrDeclare(L("idx", IrType.I32), new IrIntrinsic(IrIntrinsicOp.StrIndexOf, IrType.I32, [R(s), new IrStrConst("a")])),
      new IrDeclare(L("has", IrType.Bool), new IrIntrinsic(IrIntrinsicOp.StrContains, IrType.Bool, [R(s), new IrStrConst("a")])),
      new IrDeclare(L("rep", IrType.String), new IrIntrinsic(IrIntrinsicOp.StrReplaceAll, IrType.String, [R(s), new IrStrConst("a"), new IrStrConst("b")])),
      new IrDeclare(L("tr", IrType.String), new IrIntrinsic(IrIntrinsicOp.StrTrim, IrType.String, [R(s), I(0)])),
      new IrDeclare(L("pad", IrType.String), new IrIntrinsic(IrIntrinsicOp.StrPadStart, IrType.String, [R(s), I(5), new IrStrConst("0")])),
      new IrDeclare(sp, new IrIntrinsic(IrIntrinsicOp.StrSplit, IrType.Array(IrType.String), [R(s), new IrStrConst(","), I(-1)])),
      new IrDeclare(L("jn", IrType.String), new IrIntrinsic(IrIntrinsicOp.StrJoin, IrType.String, [new IrStrConst("-"), R(sp)])),
      new IrReturn(Bin(IrBinaryOp.Add, R(vl), R(sl))));

    var src = Emit(new IrModule("Containers", [fn]));
    Assert.That(src, Does.Contain("push(@{"));
    Assert.That(src, Does.Contain("_ply_view_slice"));
    Assert.That(src, Does.Contain("_ply_view_copy"));
    Assert.That(src, Does.Contain("_ply_view_equal"));
    Assert.That(src, Does.Contain("_ply_str_trim"));
    Assert.That(src, Does.Contain("_ply_str_pad"));

    // Reinterpret view -> PLY1767
    var u8v = L("u8v", IrType.ReadOnlySpan(IrType.U8));
    var overlayMod = Returning(IrType.ReadOnlySpan(IrType.U32), [u8v],
      new IrIntrinsic(IrIntrinsicOp.Reinterpret, IrType.ReadOnlySpan(IrType.U32), [R(u8v)]));
    Assert.That(Problems(overlayMod), Has.Some.Contains("PLY1767"));

    // Cross-type pointer cast -> PLY1766
    var u8a = L("u8a", IrType.Array(IrType.U8));
    var ptr = L("ptr", IrType.Pointer(IrType.U8));
    var ptrCast = L("pc", IrType.Pointer(IrType.U32));
    var badPtrFn = Function("bad_ptr", IrType.I32, [u8a],
      new IrDeclare(ptr, new IrIntrinsic(IrIntrinsicOp.Pin, IrType.Pointer(IrType.U8), [R(u8a)])),
      new IrDeclare(ptrCast, new IrIntrinsic(IrIntrinsicOp.PointerCast, IrType.Pointer(IrType.U32), [R(ptr)])),
      new IrReturn(I(0)));
    Assert.That(Problems(new IrModule("BadPtr", [badPtrFn])), Has.Some.Contains("PLY1766"));
  }

}
