using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Polypiler.Core.Backends.VBScript;
using Polypiler.Core.IR;
using static Polypiler.Tests.Backends.VBScript.VBScriptTestHost;

namespace Polypiler.Tests.Backends.VBScript;

/// <summary>Tests for VBScript profiles, naming, diagnostics (<c>PLY2650</c>–<c>PLY2652</c>) and error paths.</summary>
[TestFixture]
public sealed class VBScriptProfileAndNamingTests {

  [Test]
  public void Profile_ParsesDefaultAndNamed_AndRejectsUnknown() {
    Assert.That(VBScriptProfile.Default, Is.EqualTo(VBScriptProfile.Vbs58));
    Assert.That(VBScriptProfile.Names, Is.EqualTo(new[] { "vbscript/5.8" }));
    Assert.That(VBScriptProfile.Vbs58.ToString(), Is.EqualTo("vbscript/5.8"));
    Assert.That(VBScriptEmitter.Runtime, Is.EqualTo("cscript"));

    Assert.That(VBScriptProfile.Parse(), Is.EqualTo(VBScriptProfile.Vbs58));
    Assert.That(VBScriptProfile.Parse("vbscript/5.8"), Is.EqualTo(VBScriptProfile.Vbs58));

    Assert.That(VBScriptProfile.TryParse(null, out var p1, out var e1), Is.True);
    Assert.That(p1, Is.EqualTo(VBScriptProfile.Vbs58));
    Assert.That(e1, Is.Null);

    Assert.That(VBScriptProfile.TryParse("vbscript/6.0", out var p2, out var e2), Is.False);
    Assert.That(p2, Is.Null);
    Assert.That(e2, Does.Contain("vbscript/6.0").And.Contain("vbscript/5.8"));

    Assert.That(() => VBScriptProfile.Parse("vbscript/9.9"), Throws.ArgumentException.With.Message.Contain("vbscript/9.9"));
  }

  [Test]
  public void ScriptFileName_RespectsOptionsAndSanitizes() {
    var mod = Returning(IrType.I32, [], I(1));
    Assert.That(VBScriptEmitter.Emit(mod)[0].Path, Is.EqualTo("M.vbs"));
    Assert.That(VBScriptEmitter.Emit(mod, null, new() { ScriptName = "custom_out" })[0].Path, Is.EqualTo("custom_out.vbs"));
    Assert.That(VBScriptEmitter.Emit(mod, null, new() { ScriptName = "already.vbs" })[0].Path, Is.EqualTo("already.vbs"));
    Assert.That(VBScriptEmitter.Emit(mod, null, new() { ScriptName = "a/b c" })[0].Path, Is.EqualTo("a_b_c.vbs"));

    var dotMod = new IrModule("...", [Function("f", IrType.I32, [], new IrReturn(I(1)))]);
    Assert.That(VBScriptEmitter.Emit(dotMod)[0].Path, Is.EqualTo("module.vbs"));
  }

  [Test]
  public void CaseInsensitiveCollisionsAndReservedWords_AreDisambiguatedAndRunLive() {
    var a1 = L("Dim", IrType.I32);
    var a2 = L("dim", IrType.I32);
    var a3 = L("9x", IrType.I32);
    var a4 = L("ply_foo", IrType.I32);
    var a5 = L("", IrType.I32);
    var fn1 = Function("Enum", IrType.I32, [a1, a2, a3, a4, a5],
      new IrReturn(Bin(IrBinaryOp.Add, R(a1), Bin(IrBinaryOp.Add, R(a2), Bin(IrBinaryOp.Add, R(a3), Bin(IrBinaryOp.Add, R(a4), R(a5)))))));
    var fn2 = Function("enum", IrType.I32, [], new IrReturn(I(7)));
    var fn3 = Function("123", IrType.I32, [], new IrReturn(I(9)));
    var mod = new IrModule("Names", [fn1, fn2, fn3]);

    var vbs = Emit(mod);
    Assert.That(vbs, Does.StartWith("Option Explicit\n"));
    var lines = RunLines(vbs, """
      WScript.Echo Enum_v(1, 2, 3, 4, 5)
      WScript.Echo Enum_v_2()
      WScript.Echo fn_123()
      """);
    Assert.That(lines, Is.EqualTo(new[] { "15", "7", "9" }));
  }

  [Test]
  public void StringLiterals_EscapeQuotesControlCharsAndUnicode() {
    var fn = Function("getStr", IrType.String, [],
      new IrReturn(new IrStrConst("a\"b\nc\rd\t\u00E9")));
    var fnEmpty = Function("getEmpty", IrType.String, [], new IrReturn(new IrStrConst("")));
    var vbs = Emit(new IrModule("Strs", [fn, fnEmpty]));
    Assert.That(vbs, Does.Contain("\"\""));
    Assert.That(vbs, Does.Contain("Chr(10)"));
    Assert.That(vbs, Does.Contain("Chr(13)"));
    Assert.That(vbs, Does.Contain("Chr(9)"));
    Assert.That(vbs, Does.Contain("ChrW(233)"));

    var lines = RunLines(vbs, """
      Dim s
      s = getStr()
      WScript.Echo Len(s)
      WScript.Echo AscW(Mid(s, 2, 1))
      WScript.Echo AscW(Mid(s, 4, 1))
      WScript.Echo AscW(Mid(s, 6, 1))
      WScript.Echo AscW(Mid(s, 8, 1))
      WScript.Echo AscW(Mid(s, 9, 1))
      WScript.Echo Len(getEmpty())
      """);
    Assert.That(lines, Is.EqualTo(new[] { "9", "34", "10", "13", "9", "233", "0" }));
  }

  [Test]
  public void NullArgumentsAndInvalidIr_ThrowOrReport() {
    Assert.That(() => VBScriptEmitter.Check(null!), Throws.ArgumentNullException);
    Assert.That(() => VBScriptEmitter.Emit(null!), Throws.ArgumentNullException);
    Assert.That(() => VBScriptEmitter.EmitTarget(null!), Throws.ArgumentNullException);
    Assert.That(() => VBScriptEmitter.Legalize(null!, () => new IrModule("L", [])), Throws.ArgumentNullException);
    Assert.That(() => VBScriptEmitter.Legalize(Returning(IrType.I32, [], I(0)), null!), Throws.ArgumentNullException);

    // Unverified IR (mismatched return type).
    var broken = new IrModule("Broken", [Function("bad", IrType.I32, [], new IrReturn(B(true)))]);
    Assert.That(VBScriptEmitter.Check(broken), Is.Not.Empty);
    Assert.That(() => VBScriptEmitter.Emit(broken), Throws.ArgumentException);
  }

  [Test]
  public void UnsupportedTypes_ReportPLY2651() {
    var big = L("b", IrType.BigInt);
    var bigMod = new IrModule("Big", [Function("f", IrType.BigInt, [big], new IrReturn(R(big)))]);
    var bigDiags = VBScriptEmitter.Check(bigMod);
    Assert.That(bigDiags.Select(d => d.Code), Has.Member(VBScriptEmitter.UnsupportedTypeCode));
    Assert.That(() => VBScriptEmitter.Emit(bigMod), Throws.ArgumentException);

    var list = L("xs", IrType.List(IrType.I32));
    var listMod = new IrModule("Lst", [Function("f", IrType.I32, [list], new IrReturn(I(0)))]);
    Assert.That(VBScriptEmitter.Check(listMod).Select(d => d.Code), Has.Member(VBScriptEmitter.UnsupportedTypeCode));

    var span = L("sp", IrType.Span(IrType.U8));
    var spanMod = new IrModule("Sp", [Function("f", IrType.I32, [span], new IrReturn(I(0)))]);
    Assert.That(VBScriptEmitter.Check(spanMod).Select(d => d.Code), Has.Member(VBScriptEmitter.UnsupportedTypeCode));

    var fnType = IrType.Function(IrType.I32, IrType.I32);
    var fnLocal = L("cb", fnType);
    var fnMod = new IrModule("Fn", [Function("f", IrType.I32, [fnLocal], new IrReturn(I(0)))]);
    Assert.That(VBScriptEmitter.Check(fnMod).Select(d => d.Code), Has.Member(VBScriptEmitter.UnsupportedTypeCode));

    var asyncMod = new IrModule("Asy", [Function("f", IrType.Async(IrType.I32), [], new IrReturn(I(1)))]);
    Assert.That(VBScriptEmitter.Check(asyncMod).Select(d => d.Code), Has.Member(VBScriptEmitter.UnsupportedTypeCode));
  }

  [Test]
  public void UnsupportedConstructs_ReportPLY2650() {
    // Foreign function on module.
    var foreign = new IrForeignFunction("puts", true, "libc", IrType.I32, []);
    var fMod = new IrModule("For", [Function("f", IrType.I32, [], new IrReturn(new IrForeignCall("puts", IrType.I32, [])))]) {
      ForeignFunctions = [foreign]
    };
    Assert.That(VBScriptEmitter.Check(fMod).Select(d => d.Code), Has.Member(VBScriptEmitter.UnsupportedConstructCode));

    // IrUsing statement imported from C#.
    var usingMod = ImportCSharpRaw("""
      using System;
      public sealed class Res : IDisposable {
        public void Dispose() { }
      }
      public static class M {
        public static int F() {
          using (var r = new Res()) {
            return 1;
          }
        }
      }
      """);
    Assert.That(VBScriptEmitter.Check(usingMod).Select(d => d.Code), Has.Member(VBScriptEmitter.UnsupportedConstructCode));

    // BigInt constant expression.
    var bigConstMod = new IrModule("Bc", [Function("f", IrType.I32, [], new IrExprStmt(new IrBigConst(BigInteger.One)), new IrReturn(I(0)))]);
    Assert.That(VBScriptEmitter.Check(bigConstMod).Select(d => d.Code), Has.Member(VBScriptEmitter.UnsupportedTypeCode));
  }

  [Test]
  public void UnsupportedIntrinsics_ReportPLY2652() {
    var x = L("x", IrType.F64);
    var sinMod = Returning(IrType.F64, [x], new IrIntrinsic(IrIntrinsicOp.Sin, IrType.F64, [R(x)]));
    Assert.That(VBScriptEmitter.Check(sinMod).Select(d => d.Code), Has.Member(VBScriptEmitter.UnsupportedIntrinsicCode));

    var arr = L("b", IrType.Array(IrType.U8));
    var load64 = Returning(IrType.U64, [arr], new IrIntrinsic(IrIntrinsicOp.LoadLittle, IrType.U64, [R(arr), I(0)]));
    Assert.That(VBScriptEmitter.Check(load64).Select(d => d.Code), Has.Member(VBScriptEmitter.UnsupportedIntrinsicCode));

    var store64 = new IrModule("S64", [
      Function("f", IrType.Void, [arr],
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.StoreBig, IrType.Void, [R(arr), I(0), C(IrType.U64, 1)])))
    ]);
    Assert.That(VBScriptEmitter.Check(store64).Select(d => d.Code), Has.Member(VBScriptEmitter.UnsupportedIntrinsicCode));
  }

  [Test]
  public void UnusedHelpers_AreNotEmitted() {
    var a = L("a", IrType.I32);
    var vbs = Emit(Returning(IrType.I32, [a], R(a)));
    Assert.That(vbs, Does.Not.Contain("ply_i32"));
    Assert.That(vbs, Does.Not.Contain("ply_str_split"));
    Assert.That(vbs, Does.Not.Contain("ply_Exception"));
  }

}
