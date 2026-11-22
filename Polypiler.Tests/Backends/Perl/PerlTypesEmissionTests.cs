using System.Collections.Generic;
using NUnit.Framework;
using Polypiler.Core.Backends.Perl;
using Polypiler.Core.IR;
using static Polypiler.Tests.Backends.Perl.PerlIr;

namespace Polypiler.Tests.Backends.Perl;

/// <summary>Tests for Perl compound types (#434), CPAN layout &amp; Test::More (#431), traps/eval/finally and refusals.</summary>
[TestFixture]
public sealed class PerlTypesEmissionTests {

  [Test]
  public void Structs_Enums_Optionals_Maps_AndSets() {
    var pt = new IrStructType("Point");
    var fX = new IrField("X", IrType.I32, false);
    var fY = new IrField("Y", IrType.I32, false);
    pt.Define([fX, fY]);
    var color = new IrEnumType("Color", IrType.I32, [new("Red", 0), new("Green", 1), new("Blue", 2)]) { IsPublic = true };

    var mapType = IrType.Map(IrType.String, IrType.I32, MapOrder.Insertion);
    var setType = IrType.Set(IrType.I32, MapOrder.Insertion);
    var p1 = L("p1", IrType.Struct(pt));
    var p2 = L("p2", IrType.Struct(pt));
    var opt = L("opt", IrType.Optional(IrType.I32));
    var map = L("map", mapType);
    var set = L("set", setType);
    var ov = L("ov", IrType.I32);
    var mk = L("mk", IrType.String);
    var mv = L("mv", IrType.I32);

    var fn = Function("use_types", IrType.I32, [opt],
      new IrDeclare(p1, new IrStructNew(IrType.Struct(pt), [I(1), I(2)])),
      new IrDeclare(p2, R(p1)),
      new IrFieldStore(R(p2), fX, I(9)),
      new IrDeclare(L("same", IrType.Bool), new IrStructEquals(R(p1), R(p2))),
      new IrDeclare(L("c", IrType.Enum(color)), new IrConst(IrType.Enum(color), 1L)),
      new IrDeclare(ov, new IrCoalesce(R(opt), I(42))),
      new IrDeclare(L("none", IrType.Bool), new IrIsNone(R(opt))),
      new IrDeclare(L("some", IrType.Optional(IrType.I32)), new IrSome(I(7))),
      new IrDeclare(L("absent", IrType.Optional(IrType.I32)), new IrNone(IrType.Optional(IrType.I32))),
      new IrDeclare(map, new IrCollection(IrCollectionOp.MapNew, mapType, [])),
      new IrExprStmt(new IrCollection(IrCollectionOp.MapPut, IrType.Void, [R(map), new IrStrConst("k"), I(5)])),
      new IrDeclare(L("hasK", IrType.Bool), new IrCollection(IrCollectionOp.MapContainsKey, IrType.Bool, [R(map), new IrStrConst("k")])),
      new IrDeclare(L("got", IrType.I32), new IrCollection(IrCollectionOp.MapGet, IrType.I32, [R(map), new IrStrConst("k")])),
      new IrDeclare(L("mc", IrType.I32), new IrCollection(IrCollectionOp.MapCount, IrType.I32, [R(map)])),
      new IrForEachCollection(R(map), EnumerationMode.Checked, mk, mv, Block(new IrAssign(ov, Bin(IrBinaryOp.Add, R(ov), R(mv))))),
      new IrExprStmt(new IrCollection(IrCollectionOp.MapRemove, IrType.Bool, [R(map), new IrStrConst("k")])),
      new IrExprStmt(new IrCollection(IrCollectionOp.MapClear, IrType.Void, [R(map)])),
      new IrDeclare(set, new IrCollection(IrCollectionOp.SetNew, setType, [])),
      new IrExprStmt(new IrCollection(IrCollectionOp.SetAdd, IrType.Bool, [R(set), I(3)])),
      new IrDeclare(L("sc", IrType.Bool), new IrCollection(IrCollectionOp.SetContains, IrType.Bool, [R(set), I(3)])),
      new IrDeclare(L("sn", IrType.I32), new IrCollection(IrCollectionOp.SetCount, IrType.I32, [R(set)])),
      new IrExprStmt(new IrCollection(IrCollectionOp.SetRemove, IrType.Bool, [R(set), I(3)])),
      new IrExprStmt(new IrCollection(IrCollectionOp.SetClear, IrType.Void, [R(set)])),
      new IrReturn(Bin(IrBinaryOp.Add, new IrFieldLoad(R(p1), fX), R(ov))));

    var mod = new IrModule("TypesDemo", [fn]) { Types = [pt, color] };
    var src = Emit(mod);
    Assert.That(src, Does.Contain("use constant {"));
    Assert.That(src, Does.Contain("RED => 0"));
    Assert.That(src, Does.Contain("%EXPORT_TAGS"));
    Assert.That(src, Does.Contain("sub _eq_point"));
    Assert.That(src, Does.Contain("// 42"));
    Assert.That(src, Does.Contain("defined("));
    Assert.That(src, Does.Contain("exists("));
    Assert.That(src, Does.Contain("delete "));
    Assert.That(src, Does.Contain("sort keys"));
  }

  [Test]
  public void TryCatchFinally_AndReturnInsideTry_UseCompletionRecord() {
    var x = L("x", IrType.I32);
    var cleaned = L("cleaned", IrType.I32);
    var divZero = IrExceptionClass.Of(IrTrap.DivideByZero);
    var err = L("err", IrType.Object(divZero));
    var exCtor = divZero.Constructors[1];
    var fn = Function("safe_work", IrType.I32, [x],
      new IrDeclare(cleaned, I(0)),
      new IrTry(
        Block(
          new IrIf(Bin(IrBinaryOp.Equal, R(x), I(0)),
            Block(new IrThrow(new IrNew(IrType.Object(divZero), exCtor, [new IrStrConst("zero")]))),
            null),
          new IrReturn(Bin(IrBinaryOp.Divide, I(10), R(x)))),
        [new IrCatch(divZero, err, null, Block(new IrReturn(I(-1))))],
        Block(new IrAssign(cleaned, I(1)))),
      new IrReturn(R(cleaned)));

    var src = Emit(new IrModule("SafeWork", [fn]));
    Assert.That(src, Does.Contain("eval {"));
    Assert.That(src, Does.Contain("_trap_matches"));
    Assert.That(src, Does.Contain("die \"DivideByZero: "));
    Assert.That(src, Does.Contain("$_ret"));
  }

  [Test]
  public void TestFunctions_EmitTestMoreFile() {
    var a = L("a", IrType.I32);
    var b = L("b", IrType.I32);
    var helper = Function("Add", IrType.I32, [a, b],
      new IrReturn(Bin(IrBinaryOp.Add, R(a), R(b))));
    var test = Function("TestAdd", IrType.Void, [],
      new IrExprStmt(new IrCall("Add", IrType.I32, [I(2), I(3)])),
      new IrReturn(null));
    var mod = new IrModule("Demo.Math", [helper, test]);
    var files = EmitAll(mod, options: new PerlEmitOptions { TestFunctions = new HashSet<string> { "TestAdd" } });
    Assert.That(files, Has.Count.EqualTo(2));
    Assert.That(files[0].Path, Is.EqualTo("lib/Demo/Math.pm"));
    Assert.That(files[1].Path, Is.EqualTo("t/math.t"));
    Assert.That(files[1].Content, Does.Contain("use Test::More;"));
    Assert.That(files[1].Content, Does.Contain("done_testing;"));
  }

  [Test]
  public void Interfaces_EmitAcrossObjectModels_AndExportedFunctionRefsAreRefused() {
    var iface = new IrInterfaceType("IShape");
    var mAreaSig = new IrMethod("Area", "IShape__area", false) { Dispatch = IrDispatch.Abstract };
    iface.DefineMembers([mAreaSig], [], []);

    var cls = new IrClassType("Square");
    var fSide = new IrField("Side", IrType.I32, true) { Visibility = IrVisibility.Private };
    cls.Inherit(null, [iface]);
    cls.Define([fSide]);
    var ctor = new IrConstructor("Square__ctor");
    var mArea = new IrMethod("Area", "Square__area", false) { Dispatch = IrDispatch.Virtual, Implements = [mAreaSig] };
    cls.DefineMembers([ctor], [mArea], [], []);

    var self = L("self", IrType.Object(cls));
    var s = L("s", IrType.I32);
    var ifaceSelf = L("self", IrType.Object(iface));
    var shapeParam = L("shape", IrType.Object(iface));
    var mod = new IrModule("Shapes", [
      Function("IShape__area", false, IrType.I32, [ifaceSelf]),
      Function("Square__ctor", false, IrType.Void, [self, s], new IrFieldStore(R(self), fSide, R(s)), new IrReturn(null)),
      Function("Square__area", false, IrType.I32, [self], new IrReturn(Bin(IrBinaryOp.Multiply, new IrFieldLoad(R(self), fSide), new IrFieldLoad(R(self), fSide)))),
      Function("measure", IrType.I32, [shapeParam],
        new IrDeclare(L("isSq", IrType.Bool), new IrTypeTest(R(shapeParam), IrType.Object(cls))),
        new IrDeclare(L("isSh", IrType.Bool), new IrTypeTest(R(shapeParam), IrType.Object(iface))),
        new IrDeclare(L("asSq", IrType.Object(cls)), new IrAsCast(IrType.Object(cls), R(shapeParam))),
        new IrDeclare(L("castSq", IrType.Object(cls)), new IrCast(IrType.Object(cls), R(shapeParam))),
        new IrReturn(new IrMethodCall(IrType.I32, mAreaSig, R(shapeParam), []) { Kind = IrMethodCallKind.Interface }))
    ]) { Types = [iface, cls] };

    var blessedSrc = Emit(mod, PerlProfile.Default);
    Assert.That(blessedSrc, Does.Contain("package Shapes::Square;").And.Contain("sub _ply_does_i_shape").And.Contain("bless {"));

    var mooseSrc = Emit(mod, PerlProfile.Default with { ObjectModel = PerlObjectModel.Moose });
    Assert.That(mooseSrc, Does.Contain("use Moose::Role;").And.Contain("requires 'area';").And.Contain("use Moose;").And.Contain("with 'Shapes::IShape';").And.Contain("->does('Shapes::IShape')").And.Contain("make_immutable"));

    var insideOutSrc = Emit(mod, PerlProfile.Default with { ObjectModel = PerlObjectModel.InsideOut });
    Assert.That(insideOutSrc, Does.Contain("fieldhash my %_f_side;").And.Contain("bless \\(my $_id), $class").And.Not.Contain("sub side {"));

    var fnType = IrType.Function(IrType.I32, IrType.I32);
    var cb = L("cb", fnType);
    var badExport = Returning(IrType.I32, [cb], new IrInvoke(IrType.I32, R(cb), [I(1)]));
    Assert.That(Problems(badExport), Has.Some.Contains("PLY1775"));
  }

  [Test]
  public void Dialects56And58_AvoidModernSyntaxAndEmitCompatibleHelpers() {
    var color = new IrEnumType("Color", IrType.I32, [new("Red", 0), new("Green", 1)]) { IsPublic = true };
    var opt = L("opt", IrType.Optional(IrType.I32));
    var f = L("f", IrType.F64);
    var g = L("g", IrType.F64);
    var buf = L("buf", IrType.Array(IrType.U8));
    var t = L("t", IrType.F64);

    var fn = Function("compat", IrType.F64, [opt, f, g, buf],
      new IrDeclare(L("c", IrType.I32), new IrCoalesce(R(opt), I(7))),
      new IrDeclare(L("lu16", IrType.U16), new IrIntrinsic(IrIntrinsicOp.LoadLittle, IrType.U16, [R(buf), I(0)])),
      new IrDeclare(L("bu16", IrType.U16), new IrIntrinsic(IrIntrinsicOp.LoadBig, IrType.U16, [R(buf), I(0)])),
      new IrDeclare(L("le16", IrType.I16), new IrIntrinsic(IrIntrinsicOp.LoadLittle, IrType.I16, [R(buf), I(0)])),
      new IrDeclare(L("be16", IrType.I16), new IrIntrinsic(IrIntrinsicOp.LoadBig, IrType.I16, [R(buf), I(0)])),
      new IrDeclare(L("lu32", IrType.U32), new IrIntrinsic(IrIntrinsicOp.LoadLittle, IrType.U32, [R(buf), I(0)])),
      new IrDeclare(L("be32", IrType.U32), new IrIntrinsic(IrIntrinsicOp.LoadBig, IrType.U32, [R(buf), I(0)])),
      new IrDeclare(L("li32", IrType.I32), new IrIntrinsic(IrIntrinsicOp.LoadLittle, IrType.I32, [R(buf), I(0)])),
      new IrDeclare(L("bi32", IrType.I32), new IrIntrinsic(IrIntrinsicOp.LoadBig, IrType.I32, [R(buf), I(0)])),
      new IrDeclare(L("le64", IrType.U64), new IrIntrinsic(IrIntrinsicOp.LoadLittle, IrType.U64, [R(buf), I(0)])),
      new IrDeclare(L("bi64", IrType.I64), new IrIntrinsic(IrIntrinsicOp.LoadBig, IrType.I64, [R(buf), I(0)])),
      new IrDeclare(L("lf32", IrType.F32), new IrIntrinsic(IrIntrinsicOp.LoadLittle, IrType.F32, [R(buf), I(0)])),
      new IrDeclare(L("beD", IrType.F64), new IrIntrinsic(IrIntrinsicOp.LoadBig, IrType.F64, [R(buf), I(0)])),
      new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.StoreLittle, IrType.Void, [R(buf), I(0), new IrConst(IrType.I16, 1)])),
      new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.StoreBig, IrType.Void, [R(buf), I(0), new IrConst(IrType.U16, 1)])),
      new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.StoreLittle, IrType.Void, [R(buf), I(0), I(2)])),
      new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.StoreBig, IrType.Void, [R(buf), I(0), new IrConst(IrType.U32, 2)])),
      new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.StoreLittle, IrType.Void, [R(buf), I(0), new IrConvert(IrType.F32, R(f))])),
      new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.StoreBig, IrType.Void, [R(buf), I(0), R(f)])),
      new IrDeclare(t, new IrIntrinsic(IrIntrinsicOp.Truncate, IrType.F64, [R(f)])),
      new IrDeclare(L("re", IrType.F64), new IrIntrinsic(IrIntrinsicOp.RoundEven, IrType.F64, [R(f)])),
      new IrDeclare(L("ra", IrType.F64), new IrIntrinsic(IrIntrinsicOp.RoundAway, IrType.F64, [R(f)])),
      new IrDeclare(L("cs", IrType.F64), new IrIntrinsic(IrIntrinsicOp.CopySign, IrType.F64, [R(f), R(g)])),
      new IrDeclare(L("sq", IrType.F64), new IrIntrinsic(IrIntrinsicOp.Sqrt, IrType.F64, [R(f)])),
      new IrDeclare(L("mn", IrType.F64), new IrIntrinsic(IrIntrinsicOp.Min, IrType.F64, [R(f), R(g)])),
      new IrDeclare(L("mx", IrType.F64), new IrIntrinsic(IrIntrinsicOp.Max, IrType.F64, [R(f), R(g)])),
      new IrDeclare(L("b1", IrType.Bool), new IrIntrinsic(IrIntrinsicOp.IsNaN, IrType.Bool, [R(f)])),
      new IrDeclare(L("b2", IrType.Bool), new IrIntrinsic(IrIntrinsicOp.IsInfinity, IrType.Bool, [R(f)])),
      new IrDeclare(L("b3", IrType.Bool), new IrIntrinsic(IrIntrinsicOp.IsFinite, IrType.Bool, [R(f)])),
      new IrDeclare(L("b4", IrType.Bool), new IrIntrinsic(IrIntrinsicOp.IsNegative, IrType.Bool, [R(f)])),
      new IrDeclare(L("b5", IrType.Bool), new IrIntrinsic(IrIntrinsicOp.IsNormal, IrType.Bool, [R(f)])),
      new IrDeclare(L("b6", IrType.Bool), new IrIntrinsic(IrIntrinsicOp.IsSubnormal, IrType.Bool, [R(f)])),
      new IrDeclare(L("b7", IrType.Bool), new IrIntrinsic(IrIntrinsicOp.IsInteger, IrType.Bool, [R(f)])),
      new IrReturn(Bin(IrBinaryOp.Remainder, Bin(IrBinaryOp.Divide, R(f), R(g)), R(t))));

    var mod = new IrModule("Compat", [fn]) { Types = [color] };

    var p56 = Emit(mod, PerlProfile.Perl56);
    Assert.That(p56, Does.Contain("use 5.006;").And.Contain("our @ISA = qw(Exporter);").And.Contain("use constant RED => 0;"));
    Assert.That(p56, Does.Not.Contain(" // ").And.Not.Contain("POSIX::isnan").And.Not.Contain("utf8::downgrade").And.Not.Contain("d>").And.Not.Contain("Q<"));
    Assert.That(p56, Does.Contain("_ply_fdiv_f64_56").And.Contain("_ply_frem_f64_56").And.Contain("_ply_endian_bytes"));

    var p58 = Emit(mod, PerlProfile.Perl58);
    Assert.That(p58, Does.Contain("use 5.008;").And.Contain("our @ISA = qw(Exporter);").And.Contain("use constant {"));
    Assert.That(p58, Does.Not.Contain(" // ").And.Not.Contain("POSIX::isnan").And.Not.Contain("d>"));

    var p56Str = Emit(mod, PerlProfile.Perl56 with { ByteStorage = PerlByteStorage.String });
    Assert.That(p56Str, Does.Not.Contain("s<").And.Not.Contain("l>").And.Not.Contain("d>"));
  }

}
