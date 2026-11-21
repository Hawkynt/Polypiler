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
  public void Interfaces_AndExportedFunctionRefs_AreRefused() {
    var iface = new IrInterfaceType("IShape");
    iface.DefineMembers([], [], []);
    var modIface = new IrModule("BadIface", [Function("f", IrType.I32, [], new IrReturn(I(0)))]) { Types = [iface] };
    Assert.That(Problems(modIface), Has.Some.Contains("PLY1782"));

    var fnType = IrType.Function(IrType.I32, IrType.I32);
    var cb = L("cb", fnType);
    var badExport = Returning(IrType.I32, [cb], new IrInvoke(IrType.I32, R(cb), [I(1)]));
    Assert.That(Problems(badExport), Has.Some.Contains("PLY1775"));
  }

}
