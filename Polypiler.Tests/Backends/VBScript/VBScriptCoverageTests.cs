using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.VBScript;
using Polypiler.Core.IR;
using static Polypiler.Tests.Backends.VBScript.VBScriptTestHost;

namespace Polypiler.Tests.Backends.VBScript;

/// <summary>Targeted emission and execution tests covering every branch of the VBScript 5.8 backend.</summary>
[TestFixture]
public sealed class VBScriptCoverageTests {

  [Test]
  public void NestedStructs_Interfaces_StaticAndVoidMethods_AbstractBase_AndFieldArrayStores_RunLive() {
    var inner = new IrStructType("Inner");
    var iv = new IrField("V", IrType.I32, false);
    inner.Define([iv]);
    var innerT = IrType.Struct(inner);

    var outer = new IrStructType("Outer");
    var oIn = new IrField("In", innerT, false);
    var oTag = new IrField("Tag", IrType.I32, false);
    outer.Define([oIn, oTag]);
    var outerT = IrType.Struct(outer);

    var iface = new IrInterfaceType("IGreet");
    var iSay = new IrMethod("Say", "IGreet_Say", false) { Dispatch = IrDispatch.Abstract };
    iface.DefineMembers([iSay], [], []);

    var baseCls = new IrClassType("BaseBox") { IsAbstract = true };
    var bArr = new IrField("Data", IrType.Array(IrType.I32), false);
    var bFlag = new IrField("Flag", IrType.Bool, false);
    var bScore = new IrField("Score", IrType.F64, false);
    baseCls.Define([bArr, bFlag, bScore]);
    baseCls.Inherit(null, [iface]);
    var bCtor = new IrConstructor("BaseBox_ctor");
    var bAbs = new IrMethod("Say", "BaseBox_Say", false) { Dispatch = IrDispatch.Abstract, Implements = [iSay] };
    var bPing = new IrMethod("Ping", "BaseBox_Ping", false);
    var bHelper = new IrMethod("StaticSum", "BaseBox_StaticSum", true);
    baseCls.DefineMembers([bCtor], [bAbs, bPing, bHelper], [], []);

    var subCls = new IrClassType("SubBox") { IsRecord = true };
    var sIn = new IrField("Meta", innerT, false);
    subCls.Define([sIn]);
    subCls.Inherit(baseCls, []);
    var sCtor = new IrConstructor("SubBox_ctor");
    var sSay = new IrMethod("Say", "SubBox_Say", false) { Dispatch = IrDispatch.Override, Overrides = bAbs };
    subCls.DefineMembers([sCtor], [sSay], [], []);

    var thisI = L("this", IrType.Object(iface));
    var thisB = L("this", IrType.Object(baseCls));
    var thisS = L("this", IrType.Object(subCls));
    var aP = L("a", IrType.I32);
    var bP = L("b", IrType.I32);
    var o1 = L("o1", outerT);
    var o2 = L("o2", outerT);
    var s1 = L("s1", IrType.Object(subCls));
    var s2 = L("s2", IrType.Object(subCls));
    var iRef = L("iRef", IrType.Object(iface));
    var bRef = L("bRef", IrType.Object(baseCls));
    var arrP = L("arr", IrType.Array(IrType.I32));
    var strL = L("st", IrType.String);
    var gUninit = new IrGlobal("UninitG", IrType.I32, null);
    var gInit = new IrGlobal("InitG", IrType.I32, Bin(IrBinaryOp.Add, I(1), new IrConditional(B(true), I(2), I(3))));

    var kStrArr = IrModuleConst.OfStrings("Words", IrType.Array(IrType.String), ["one", "two"]);
    var kU64 = new IrModuleConst("BigK", IrType.U64, [100]);
    var kPlainStr = IrModuleConst.OfStrings("Plain", IrType.String, ["plain"]);

    var mod = new IrModule("CovObj", [
      Function("IGreet_Say", false, IrType.I32, [thisI]),
      Function("BaseBox_ctor", false, IrType.Void, [thisB],
        new IrFieldStore(R(thisB), bArr, new IrNewArray(IrType.Array(IrType.I32), I(3)))),
      Function("BaseBox_Say", false, IrType.I32, [thisB]),
      Function("BaseBox_Ping", false, IrType.Void, [thisB],
        new IrStore(new IrFieldLoad(R(thisB), bArr), I(1), I(42)),
        new IrReturn(null)),
      Function("BaseBox_StaticSum", false, IrType.I32, [aP, bP],
        new IrReturn(Bin(IrBinaryOp.Add, R(aP), R(bP)))),
      Function("SubBox_ctor", false, IrType.Void, [thisS],
        new IrExprStmt(new IrCall("BaseBox_ctor", IrType.Void, [R(thisS)])),
        new IrFieldStore(R(thisS), sIn, new IrStructNew(innerT, [I(7)]))),
      Function("SubBox_Say", false, IrType.I32, [thisS],
        new IrReturn(new IrArrayLoad(new IrFieldLoad(R(thisS), bArr), I(1)))),
      Function("initMod", false, IrType.Void, [],
        new IrGlobalAssign(gUninit, I(11))),
      Function("runAll", IrType.I32, [],
        new IrDeclare(o1, new IrStructNew(outerT, [new IrStructNew(innerT, [I(5)]), I(9)])),
        new IrDeclare(o2, R(o1)),
        new IrFieldStore(new IrFieldLoad(R(o2), oIn), iv, I(88)),
        new IrDeclare(s1, new IrNew(IrType.Object(subCls), sCtor, [])),
        new IrDeclare(s2, new IrNew(IrType.Object(subCls), sCtor, [])),
        new IrExprStmt(new IrMethodCall(IrType.Void, bPing, R(s1), [])),
        new IrDeclare(iRef, R(s1)),
        new IrDeclare(bRef, new IrAsCast(IrType.Object(baseCls), R(iRef))),
        new IrDeclare(arrP, new IrNewArray(IrType.Array(IrType.I32), I(2))),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayClear, IrType.Void, [R(arrP)])),
        new IrExprStmt(Bin(IrBinaryOp.Add, I(1), I(2))),
        new IrDeclare(strL, new IrNull(IrType.String)),
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrMethodCall(IrType.I32, iSay, R(iRef), []) { Kind = IrMethodCallKind.Interface },
          Bin(IrBinaryOp.Add,
            new IrMethodCall(IrType.I32, bHelper, null, [I(3), I(4)]),
            Bin(IrBinaryOp.Add,
              new IrFieldLoad(new IrFieldLoad(R(o1), oIn), iv),
              Bin(IrBinaryOp.Add,
                new IrFieldLoad(new IrFieldLoad(R(o2), oIn), iv),
                Bin(IrBinaryOp.Add,
                  new IrConditional(new IrStructEquals(R(o1), R(o2)), I(0), I(1)),
                  Bin(IrBinaryOp.Add,
                    new IrConditional(new IrReferenceEquals(R(strL), new IrNull(IrType.String)), I(2), I(0)),
                    Bin(IrBinaryOp.Add,
                      new IrConditional(new IrIsNull(R(bRef)), I(0), I(3)),
                      Bin(IrBinaryOp.Add,
                        new IrConditional(new IrStructEquals(R(s1), R(s2)), I(4), I(0)),
                        Bin(IrBinaryOp.Add, new IrGlobalRef(gUninit),
                          Bin(IrBinaryOp.Add, new IrGlobalRef(gInit),
                            Bin(IrBinaryOp.Add, new IrConvert(IrType.I32, new IrConstRef(kU64)),
                              new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [new IrArrayLoad(new IrConstRef(kStrArr), I(1))]))))))))))))))
    ], [kStrArr, kU64, kPlainStr]) {
      Types = [inner, outer, iface, baseCls, subCls],
      Globals = [gUninit, gInit],
      Initializer = "initMod"
    };

    var vbs = Emit(mod);
    var lines = RunLines(vbs, "WScript.Echo runAll()");
    Assert.That(lines, Is.EqualTo(new[] { FormatExpected(IrInterpreter.Call(mod, "runAll", [])) }), vbs);
  }

  [Test]
  public void ControlFlowAndExceptionEdgeBranches_CoverAllPaths() {
    var i = L("i", IrType.I32);
    var x = L("x", IrType.I32);
    var f = L("f", IrType.F64);
    var arr = L("arr", IrType.Array(IrType.I32));
    var g = new IrGlobal("GStep", IrType.I32, I(10));
    var ex = L("ex", IrExceptionClass.Reference);
    var opt = L("opt", IrType.Optional(IrType.I32));

    var st = new IrStructType("S");
    var sf = new IrField("V", IrType.I32, false);
    st.Define([sf]);
    var stT = IrType.Struct(st);
    var optS = L("optS", IrType.Optional(stT));

    var custEx = new IrClassType("CustomEx");
    custEx.Define([]);
    custEx.Inherit(IrExceptionClass.Exception, []);
    var custCtor = new IrConstructor("CustomEx_ctor");
    custEx.DefineMembers([custCtor], [], [], []);
    var thisE = L("this", IrType.Object(custEx));

    var mod = new IrModule("Edges", [
      Function("CustomEx_ctor", false, IrType.Void, [thisE],
        new IrExprStmt(new IrCall(IrExceptionClass.Constructor(IrExceptionClass.Exception, 0).Function, IrType.Void, [R(thisE)]))),
      // Void return inside try/finally + nested try/finally returning value
      Function("voidTry", IrType.Void, [x],
        new IrTry(
          Block(new IrIf(Bin(IrBinaryOp.Greater, R(x), I(0)), Block(new IrReturn(null)), null)),
          [],
          Block(new IrGlobalAssign(g, Bin(IrBinaryOp.Add, new IrGlobalRef(g), I(1)))))),
      Function("nestedTryRet", IrType.I32, [x],
        new IrTry(
          Block(new IrTry(
            Block(new IrReturn(R(x))),
            [],
            Block(new IrGlobalAssign(g, Bin(IrBinaryOp.Add, new IrGlobalRef(g), I(2)))))),
          [],
          Block(new IrGlobalAssign(g, Bin(IrBinaryOp.Add, new IrGlobalRef(g), I(3)))))),
      // Simple DoWhile (no pre), counted For (i <= n and i = i + 1), non-counted For (i modified in body + pre cond)
      Function("loopVariants", IrType.I32, [x],
        new IrDeclare(i, I(0)),
        new IrDoWhile(Block(new IrAssign(i, Bin(IrBinaryOp.Add, R(i), I(1)))), Bin(IrBinaryOp.Less, R(i), I(3))),
        new IrDeclare(arr, new IrNewArray(IrType.Array(IrType.I32), I(2))),
        new IrExprStmt(new IrStep(new IrArrayLoad(R(arr), I(0)), true, false, true)),
        new IrExprStmt(new IrStep(new IrGlobalRef(g), true, true, false)),
        new IrDeclare(f, F64(1.5)),
        new IrExprStmt(new IrStep(R(f), true, false, false)),
        // Counted <= with i = i + 1 and break -> Exit For
        new IrBlock([
          new IrDeclare(L("k", IrType.I32), I(0))
        ]),
        new IrFor(Bin(IrBinaryOp.LogicalAnd, Bin(IrBinaryOp.Less, R(i), R(x)), Bin(IrBinaryOp.Less, R(i), I(10))),
          Block(new IrAssign(i, Bin(IrBinaryOp.Add, R(i), I(1)))),
          Block(new IrAssign(i, Bin(IrBinaryOp.Add, R(i), I(1))))),
        // Simple ElseIf with no else
        new IrIf(Bin(IrBinaryOp.Equal, R(x), I(1)),
          Block(new IrAssign(i, I(10))),
          Block(new IrIf(Bin(IrBinaryOp.Equal, R(x), I(2)), Block(new IrAssign(i, I(20))), null))),
        // Switch with continue inside outer switch-wrapped loop
        new IrWhile(Bin(IrBinaryOp.Less, R(i), I(25)),
          Block(
            new IrAssign(i, Bin(IrBinaryOp.Add, R(i), I(1))),
            new IrSwitch(R(i), [
              new([21], false, Block(new IrIf(B(true), Block(new IrBreak()), null))),
              new([22], false, Block(new IrContinue()))
            ]))),
        new IrReturn(R(i))),
      // Optionals, Unwrap (tested & untested), ThrowExpression, GenericEquals/Compare, EnumNames/Values, Bit ops, Strings, Floats
      Function("miscOps", IrType.I32, [opt, optS],
        new IrDeclare(x, new IrConditional(new IrIsNone(R(opt)),
          I(0),
          Bin(IrBinaryOp.Add, new IrUnwrap(R(opt)) { IsTested = true }, new IrUnwrap(R(opt))))),
        new IrDeclare(i, new IrConditional(new IrIsNone(R(optS)),
          I(0),
          new IrFieldLoad(new IrUnwrap(R(optS)), sf))),
        new IrReturn(Bin(IrBinaryOp.Add, R(x),
          Bin(IrBinaryOp.Add, R(i),
            Bin(IrBinaryOp.Add,
              new IrConditional(new IrGenericEquals(I(1), I(1)), I(1), I(0)),
              Bin(IrBinaryOp.Add,
                new IrConditional(new IrGenericEquals(F64(1.5), F64(1.5)), I(1), I(0)),
                Bin(IrBinaryOp.Add,
                  new IrConditional(new IrStructEquals(new IrStructDefault(stT), new IrStructDefault(stT)), I(1), I(0)),
                  Bin(IrBinaryOp.Add,
                    new IrGenericCompare(I(2), I(1)),
                    new IrGenericCompare(F64(2.5), F64(1.5)))))))))),
      Function("throwExprFn", IrType.I32, [x],
        new IrDeclare(i, I(0)),
        new IrTry(
          Block(new IrAssign(i, new IrConditional(Bin(IrBinaryOp.Equal, R(x), I(0)),
            new IrThrowExpression(IrType.I32, new IrNew(IrType.Object(IrExceptionClass.Exception), IrExceptionClass.Constructor(IrExceptionClass.Exception, 0), [])),
            R(x)))),
          [new IrCatch(IrExceptionClass.Exception, ex, null, Block(
            new IrAssign(i, new IrConditional(new IrIsNull(new IrFieldLoad(R(ex), IrExceptionClass.InnerException)), I(99), I(0)))))],
          null),
        new IrReturn(R(i))),
      Function("bitAndStrExtras", IrType.I32, [],
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrIntrinsic(IrIntrinsicOp.RotateLeft, IrType.I32, [I(1), I(2)]),
          Bin(IrBinaryOp.Add,
            new IrIntrinsic(IrIntrinsicOp.RotateRight, IrType.I32, [I(8), I(1)]),
            Bin(IrBinaryOp.Add,
              new IrIntrinsic(IrIntrinsicOp.PopCount, IrType.I32, [C(IrType.U8, 7)]),
              Bin(IrBinaryOp.Add,
                new IrIntrinsic(IrIntrinsicOp.PopCount, IrType.I32, [C(IrType.U16, 15)]),
                Bin(IrBinaryOp.Add,
                  new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [new IrIntrinsic(IrIntrinsicOp.StrFromCodeUnit, IrType.String, [C(IrType.U16, 65)])]),
                  Bin(IrBinaryOp.Add,
                    new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [new IrIntrinsic(IrIntrinsicOp.StrFromCodeUnits, IrType.String, [new IrIntrinsic(IrIntrinsicOp.StrToCodeUnits, IrType.Array(IrType.U16), [new IrStrConst("xy")])])]),
                    new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [new IrIntrinsic(IrIntrinsicOp.FormatEcma, IrType.String, [I(123)])]))))))))),
      Function("specialFloats", IrType.F64, [],
        new IrReturn(Bin(IrBinaryOp.Add, F64(double.NaN), Bin(IrBinaryOp.Add, F64(double.PositiveInfinity), F64(double.NegativeInfinity)))))
    ]) {
      Types = [st, custEx],
      Globals = [g]
    };

    var vbs = Emit(mod);
    Assert.That(vbs, Does.Contain("Loop While "));
    Assert.That(vbs, Does.Contain("1.79769313486231E+308"));

    var lines = RunLines(vbs, """
      Call voidTry(1)
      WScript.Echo nestedTryRet(5)
      WScript.Echo GStep
      WScript.Echo loopVariants(6)
      WScript.Echo miscOps(7, Nothing)
      WScript.Echo throwExprFn(0)
      WScript.Echo bitAndStrExtras()
      """);
    Assert.That(lines, Is.EqualTo(new[] { "5", "16", "25", "19", "99", "21" }));
  }

  [Test]
  public void EnumNamesValues_Checked64Bit_AndCSharpClassImport_EmitAndRun() {
    var dir = new IrEnumType("Dir", IrType.I32, [new("Up", 0), new("Down", 1)]);
    var dType = IrType.Enum(dir);
    var a64 = L("a", IrType.I64);
    var b64 = L("b", IrType.I64);
    var mod = new IrModule("EnumAnd64", [
      Function("namesLen", IrType.I32, [], new IrReturn(new IrArrayLength(new IrIntrinsic(IrIntrinsicOp.EnumNames, IrType.Array(IrType.String), [C(dType, 0)])))),
      Function("valsLen", IrType.I32, [], new IrReturn(new IrArrayLength(new IrIntrinsic(IrIntrinsicOp.EnumValues, IrType.Array(dType), [C(dType, 0)])))),
      Function("chk64", IrType.I64, [a64, b64], new IrReturn(Bin(IrBinaryOp.AddChecked, R(a64), R(b64)))),
      Function("u32BigLit", IrType.U32, [], new IrReturn(U(3000000000u))),
      Function("sameConv", IrType.I32, [], new IrReturn(new IrConvert(IrType.I32, I(5))))
    ]) { Types = [dir] };

    var vbs = Emit(mod);
    var lines = RunLines(vbs, """
      WScript.Echo namesLen()
      WScript.Echo valsLen()
      WScript.Echo chk64(10, 20)
      WScript.Echo u32BigLit()
      WScript.Echo sameConv()
      """);
    Assert.That(lines, Is.EqualTo(new[] { "2", "2", "30", "3000000000", "5" }));
  }

  [Test]
  public void CSharpFrontendImport_CountedForWithBreak_AndTryFinallyCatch_RunLive() {
    var mod = ImportCSharp("""
      public static int FindFirst(int[] arr, int target) {
        int found = -1;
        for (int i = 0; i < arr.Length; i = i + 1) {
          if (arr[i] == target) {
            found = i;
            break;
          }
        }
        return found;
      }

      public static int SafeDiv(int a, int b) {
        int res = 0;
        try {
          res = a / b;
        } catch (System.DivideByZeroException) {
          res = -999;
        } finally {
          res = res + 1;
        }
        return res;
      }
      """);

    var vbs = LegalizedEmit(mod);
    Assert.That(vbs, Does.Contain("Exit Do"));

    var idx = L("i", IrType.I32);
    var lim = L("n", IrType.I32);
    var acc = L("acc", IrType.I32);
    var forMod = new IrModule("ForExit", [
      Function("sumUntil3", IrType.I32, [lim],
        new IrDeclare(acc, I(0)),
        new IrDeclare(idx, I(0)),
        new IrFor(Bin(IrBinaryOp.Less, R(idx), R(lim)),
          Block(
            new IrIf(Bin(IrBinaryOp.Equal, R(idx), I(3)), Block(new IrBreak()), null),
            new IrAssign(acc, Bin(IrBinaryOp.Add, R(acc), R(idx)))),
          Block(new IrAssign(idx, Bin(IrBinaryOp.Add, R(idx), I(1))))),
        new IrReturn(R(acc)))
    ]);
    var forVbs = Emit(forMod);
    Assert.That(forVbs, Does.Contain("Exit For"));

    var lines = RunLines(vbs, """
      WScript.Echo FindFirst(Array(5, 10, 15, 20), 15)
      WScript.Echo FindFirst(Array(5, 10, 15, 20), 99)
      WScript.Echo SafeDiv(20, 4)
      WScript.Echo SafeDiv(20, 0)
      """);
    Assert.That(lines, Is.EqualTo(new[] { "2", "-1", "6", "-998" }));
    Assert.That(RunLines(forVbs, "WScript.Echo sumUntil3(10)"), Is.EqualTo(new[] { "3" }));
  }

  [Test]
  public void Legalize_RewritesNonNativeFloatAndIntegerIntrinsics() {
    var x = L("x", IrType.F64);
    var sinMod = Returning(IrType.F64, [x], new IrIntrinsic(IrIntrinsicOp.Sin, IrType.F64, [R(x)]));
    var lowered = VBScriptEmitter.Legalize(sinMod, () => Polypiler.Cli.PrecomputedImplementations.Module);
    Assert.That(lowered.Functions.Count, Is.GreaterThan(1));
  }

  [Test]
  public void ControlFlowBranches_TryBreakContinue_PreCondLoops_AndForVariants_EmitAndRun() {
    var i = L("i", IrType.I32);
    var i2 = L("i2", IrType.I32);
    var i3 = L("i3", IrType.I32);
    var i64 = L("j", IrType.I64);
    var n = L("n", IrType.I32);
    var acc = L("acc", IrType.I32);
    var ex = L("ex", IrExceptionClass.Reference);

    var mod = new IrModule("CtrlCov", [
      Function("tryLoopJumps", IrType.I32, [n],
        new IrDeclare(acc, I(0)),
        new IrDeclare(i, I(0)),
        // Loop with continue + break inside try/finally
        new IrWhile(Bin(IrBinaryOp.LogicalAnd, Bin(IrBinaryOp.Less, R(i), R(n)), Bin(IrBinaryOp.Less, R(i), I(20))),
          Block(
            new IrAssign(i, Bin(IrBinaryOp.Add, R(i), I(1))),
            new IrTry(
              Block(
                new IrIf(Bin(IrBinaryOp.Equal, R(i), I(2)), Block(new IrContinue()), null),
                new IrIf(Bin(IrBinaryOp.Equal, R(i), I(5)), Block(new IrBreak()), null),
                new IrAssign(acc, Bin(IrBinaryOp.Add, R(acc), R(i)))),
              [],
              Block(new IrAssign(acc, Bin(IrBinaryOp.Add, R(acc), I(100))))))),
        // Counted <= with IrStep increment -> For ... To bound
        new IrBlock([
          new IrDeclare(i2, I(1)),
          new IrFor(Bin(IrBinaryOp.LessOrEqual, R(i2), I(3)),
            Block(new IrAssign(acc, Bin(IrBinaryOp.Add, R(acc), R(i2)))),
            Block(new IrExprStmt(new IrStep(R(i2), true, false, false))))
        ]),
        // Non-i32 For (i64) -> falls back to general loop
        new IrBlock([
          new IrDeclare(i64, C(IrType.I64, 0)),
          new IrFor(Bin(IrBinaryOp.Less, R(i64), C(IrType.I64, 2)),
            Block(new IrAssign(acc, Bin(IrBinaryOp.Add, R(acc), I(1)))),
            Block(new IrAssign(i64, Bin(IrBinaryOp.Add, R(i64), C(IrType.I64, 1)))))
        ]),
        // For whose body modifies iv -> falls back to general loop
        new IrBlock([
          new IrDeclare(i3, I(0)),
          new IrFor(Bin(IrBinaryOp.Less, R(i3), I(2)),
            Block(new IrExprStmt(new IrStep(R(i3), true, false, false))),
            Block(new IrAssign(i3, Bin(IrBinaryOp.Add, R(i3), I(1)))))
        ]),
        // DoWhile with preCond
        new IrDoWhile(
          Block(new IrAssign(acc, Bin(IrBinaryOp.Add, R(acc), I(1)))),
          Bin(IrBinaryOp.LogicalAnd, Bin(IrBinaryOp.Less, R(acc), I(0)), Bin(IrBinaryOp.Greater, R(n), I(0)))),
        // ElseIf with preCond and Else
        new IrIf(Bin(IrBinaryOp.Equal, R(n), I(99)),
          Block(new IrAssign(acc, I(1))),
          Block(new IrIf(new IrConditional(Bin(IrBinaryOp.Equal, R(n), I(88)), B(true), B(false)),
            Block(new IrAssign(acc, I(2))),
            Block(new IrAssign(acc, Bin(IrBinaryOp.Add, R(acc), I(10))))))),
        // Nested try inside try with filter + catch/finally + continue inside try-loop
        new IrTry(
          Block(
            new IrTry(
              Block(new IrThrow(new IrNew(IrType.Object(IrExceptionClass.Exception), IrExceptionClass.Constructor(IrExceptionClass.Exception, 0), []))),
              [new IrCatch(IrExceptionClass.Exception, ex, Bin(IrBinaryOp.Equal, R(n), R(n)), Block(new IrAssign(acc, Bin(IrBinaryOp.Add, R(acc), I(1)))))],
              Block(new IrAssign(acc, Bin(IrBinaryOp.Add, R(acc), I(2)))))),
          [],
          Block(new IrAssign(acc, Bin(IrBinaryOp.Add, R(acc), I(3))))),
        new IrReturn(R(acc)))
    ]);

    var vbs = Emit(mod);
    Assert.That(vbs, Does.Contain("For "));
    var lines = RunLines(vbs, "WScript.Echo tryLoopJumps(10)");
    Assert.That(lines, Is.EqualTo(new[] { FormatExpected(IrInterpreter.Call(mod, "tryLoopJumps", [10])) }));
  }

}
