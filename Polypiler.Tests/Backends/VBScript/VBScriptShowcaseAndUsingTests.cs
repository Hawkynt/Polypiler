using System.Collections.Generic;
using NUnit.Framework;
using Polypiler.Core.Backends.VBScript;
using Polypiler.Core.IR;
using static Polypiler.Tests.Backends.VBScript.VBScriptTestHost;

namespace Polypiler.Tests.Backends.VBScript;

/// <summary>
/// Live <c>wine cscript</c> tests for VBScript <see cref="IrUsing"/> (<c>15-exceptions-and-using.cs</c>)
/// and <see cref="IrWriteLine"/> (<c>17-stdio.cs</c>), including edge cases for null resources, early return,
/// loop break/continue, exception propagation, and null/empty stdout writes.
/// </summary>
[TestFixture]
public sealed class VBScriptShowcaseAndUsingTests {

  [Test]
  public void Showcase15ExceptionsAndUsing_TranslatesAndRunsLive() {
    var mod = ImportShowcase("15-exceptions-and-using.cs");
    Assert.That(VBScriptEmitter.Check(mod), Is.Empty);

    var vbs = LegalizedEmit(mod);
    var lines = RunLines(vbs, """
      WScript.Echo Evaluate(5)
      WScript.Echo Evaluate(0)
      WScript.Echo Evaluate(-3)
      """);
    Assert.That(lines, Is.EqualTo(new[] {
      FormatExpected(IrInterpreter.Call(mod, "Evaluate", [5])),
      FormatExpected(IrInterpreter.Call(mod, "Evaluate", [0])),
      FormatExpected(IrInterpreter.Call(mod, "Evaluate", [-3]))
    }));
  }

  [Test]
  public void Using_NormalReturnBreakContinueNullNestedAndDisposeThrows_RunsLive() {
    var mod = ImportCSharpRaw("""
      using System;

      public sealed class LogResource : IDisposable {
        public static int Log = 0;
        private readonly int _id;
        private readonly bool _throwOnDispose;

        public LogResource(int id, bool throwOnDispose) {
          this._id = id;
          this._throwOnDispose = throwOnDispose;
        }

        public void Dispose() {
          Log = Log * 10 + this._id;
          if (this._throwOnDispose)
            throw new InvalidOperationException("dispose boom");
        }
      }

      public static class UsingEdgeCases {
        public static int EarlyReturn() {
          LogResource.Log = 0;
          using (var r1 = new LogResource(1, false)) {
            using (var r2 = new LogResource(2, false)) {
              return 42;
            }
          }
        }

        public static int NullResource() {
          LogResource.Log = 0;
          LogResource? r = null;
          using (r) {
            return 7;
          }
        }

        public static int LoopBreakAndContinue() {
          LogResource.Log = 0;
          int sum = 0;
          for (int i = 1; i <= 4; ++i) {
            using (var r = new LogResource(i, false)) {
              if (i == 2)
                continue;
              if (i == 4)
                break;
              sum += i;
            }
          }
          return sum * 10000 + LogResource.Log;
        }

        public static string DisposeReplacesBodyException() {
          LogResource.Log = 0;
          try {
            using (var r = new LogResource(9, true)) {
              throw new ArgumentException("body boom");
            }
          } catch (Exception e) {
            return e.Message;
          }
        }

        public static int LastLog() => LogResource.Log;
      }
      """);

    Assert.That(VBScriptEmitter.Check(mod), Is.Empty);
    var vbs = LegalizedEmit(mod);
    var lines = RunLines(vbs, """
      WScript.Echo EarlyReturn()
      WScript.Echo LastLog()
      WScript.Echo NullResource()
      WScript.Echo LastLog()
      WScript.Echo LoopBreakAndContinue()
      WScript.Echo DisposeReplacesBodyException()
      WScript.Echo LastLog()
      """);
    Assert.That(lines, Is.EqualTo(new[] { "42", "21", "7", "0", "41234", "dispose boom", "9" }));
  }

  [Test]
  public void Showcase17Stdio_AndWriteLineNull_TranslatesAndRunsLive() {
    var mod = ImportShowcase("17-stdio.cs");
    Assert.That(VBScriptEmitter.Check(mod), Is.Empty);

    var irOutput = new List<string>();
    var expectedRet = (int)IrInterpreter.Instantiate(mod, irOutput.Add).Call("Run")!;
    Assert.That(expectedRet, Is.EqualTo(14));
    Assert.That(irOutput, Is.EqualTo(new[] { "step 1: sum=1", "step 2: sum=5", "step 3: sum=14" }));

    var vbs = LegalizedEmit(mod);
    Assert.That(vbs, Does.Contain("WScript.Echo "));
    var lines = RunLines(vbs, "WScript.Echo Run()");
    Assert.That(lines, Is.EqualTo(new[] { "step 1: sum=1", "step 2: sum=5", "step 3: sum=14", "14" }));

    // Also verify IrWriteLine with literal, IrNull(String), and nullable parameter (both Null and non-null).
    var s = L("s", IrType.String);
    var nullMod = new IrModule("WriteNull", [
      Function("emitAll", IrType.Void, [s],
        new IrWriteLine(new IrStrConst("head")),
        new IrWriteLine(new IrNull(IrType.String)),
        new IrWriteLine(R(s)),
        new IrWriteLine(new IrStrConst("tail")),
        new IrReturn(null))
    ]);
    Assert.That(VBScriptEmitter.Check(nullMod), Is.Empty);
    var nullVbs = Emit(nullMod);
    var outLines = RunLines(nullVbs, """
      Call emitAll(Null)
      Call emitAll("mid")
      """);
    Assert.That(outLines, Is.EqualTo(new[] { "head", "", "", "tail", "head", "", "mid", "tail" }));
  }

  [Test]
  public void OptionalDefaultsRecordFieldsStringCharOpsAndMethodParams_RunLive() {
    var rec = new IrStructType("TagRec");
    var rVal = new IrField("Val", IrType.I32, false);
    rec.Define([rVal]);
    var recT = IrType.Struct(rec);

    var box = new IrClassType("Holder") { IsRecord = true };
    var bPeer = new IrField("Peer", IrType.Object(box), false);
    box.Define([bPeer]);
    box.Inherit(null, []);
    var bCtor = new IrConstructor("Holder_ctor");
    var bSum = new IrMethod("Sum", "Holder_Sum", false);
    box.DefineMembers([bCtor], [bSum], [], []);

    var thisB = L("this", IrType.Object(box));
    var arrP = L("xs", IrType.Array(IrType.I32));
    var scaleP = L("scale", IrType.I32);
    var uninitOpt = L("uo", IrType.Optional(IrType.I32));
    var uninitStructOpt = L("uso", IrType.Optional(recT));
    var someVal = L("sv", IrType.Optional(IrType.I32));
    var noneVal = L("nv", IrType.Optional(IrType.I32));
    var h1 = L("h1", IrType.Object(box));
    var h2 = L("h2", IrType.Object(box));
    var i = L("i", IrType.I32);
    var acc = L("acc", IrType.I32);

    var mod = new IrModule("ExtraCov", [
      Function("Holder_ctor", false, IrType.Void, [thisB, arrP],
        new IrFieldStore(R(thisB), bPeer, new IrNull(IrType.Object(box)))),
      Function("Holder_Sum", false, IrType.I32, [thisB, arrP, scaleP],
        new IrReturn(Bin(IrBinaryOp.Multiply, new IrArrayLoad(R(arrP), I(0)), R(scaleP)))),
      Function("runExtras", IrType.I32, [],
        new IrDeclare(uninitOpt, null),
        new IrDeclare(uninitStructOpt, null),
        new IrDeclare(someVal, new IrSome(I(7))),
        new IrDeclare(noneVal, new IrNone(IrType.Optional(IrType.I32))),
        new IrDeclare(h1, new IrNew(IrType.Object(box), bCtor, [new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.I32), [I(3)])])),
        new IrDeclare(h2, new IrNew(IrType.Object(box), bCtor, [new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.I32), [I(3)])])),
        new IrDeclare(acc, I(0)),
        // For loop with != condition (falls back to general loop)
        new IrDeclare(i, I(0)),
        new IrFor(Bin(IrBinaryOp.NotEqual, R(i), I(2)),
          Block(new IrAssign(acc, Bin(IrBinaryOp.Add, R(acc), I(1)))),
          Block(new IrAssign(i, Bin(IrBinaryOp.Add, R(i), I(1))))),
        new IrReturn(Bin(IrBinaryOp.Add,
          new IrMethodCall(IrType.I32, bSum, R(h1), [new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.I32), [I(4)]), I(5)]),
          Bin(IrBinaryOp.Add,
            new IrCoalesce(R(uninitOpt), I(1)),
            Bin(IrBinaryOp.Add,
              new IrCoalesce(R(someVal), I(0)),
              Bin(IrBinaryOp.Add,
                new IrCoalesce(R(noneVal), I(3)),
                Bin(IrBinaryOp.Add,
                  new IrConditional(new IrIsNone(R(uninitStructOpt)), I(2), I(0)),
                  Bin(IrBinaryOp.Add,
                    new IrConditional(new IrStructEquals(R(h1), R(h2)), I(8), I(0)),
                    Bin(IrBinaryOp.Add,
                      new IrIntrinsic(IrIntrinsicOp.StrIndexOf, IrType.I32, [new IrStrConst("banana"), C(IrType.U16, 'n')]),
                      Bin(IrBinaryOp.Add,
                        new IrIntrinsic(IrIntrinsicOp.StrLastIndexOf, IrType.I32, [new IrStrConst("banana"), new IrStrConst("na"), I(3)]),
                        R(acc)))))))))))
    ]) { Types = [rec, box] };

    Assert.That(VBScriptEmitter.Check(mod), Is.Empty);
    var vbs = Emit(mod);
    var lines = RunLines(vbs, "WScript.Echo runExtras()");
    // 20 (bSum) + 1 (uninitOpt) + 7 (someVal) + 3 (noneVal) + 2 (uninitStructOpt) + 8 (h1 == h2) + 2 (indexOf) + 2 (lastIndexOf) + 2 (acc) = 47
    Assert.That(lines, Is.EqualTo(new[] { "47" }));
  }

}
