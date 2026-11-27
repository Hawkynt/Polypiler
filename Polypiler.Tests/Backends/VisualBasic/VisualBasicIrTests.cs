using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.VisualBasic;
using Polypiler.Core.Backends.VisualBasic.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Backends.VisualBasic;

/// <summary>The Visual Basic backend on IR built by hand: the nodes no frontend of the tests produces, and every refusal of the writer.</summary>
[TestFixture]
public sealed class VisualBasicIrTests {

  private static readonly IrLocal _a = new("a", IrType.I32);
  private static readonly IrLocal _b = new("b", IrType.I32);

  private static IrConst I32(long value) => new(IrType.I32, value);

  private static IrFunction Function(IrType returns, IReadOnlyList<IrLocal> parameters, params IrStmt[] body)
    => new("F", true, returns, parameters, [], new IrBlock(body));

  private static string Emitted(IrModule module, VisualBasicProfile? profile = null) {
    var problems = VisualBasicEmitter.Check(module, profile ?? VisualBasicProfile.Default);
    Assert.That(problems, Is.Empty, string.Join("\n", problems));
    var text = VisualBasicEmitter.Emit(module, profile ?? VisualBasicProfile.Default).Single().Content;
    Assert.That(VbSyntaxRewriter.Shape(VbSyntaxReader.ReadModule(text)), Is.Not.Empty);
    return text;
  }

  private static string Problems(IrModule module, VisualBasicProfile? profile = null)
    => string.Join("\n", VisualBasicEmitter.Check(module, profile ?? VisualBasicProfile.Default).Select(problem => problem.ToString()));

  [Test]
  public void StructEquality_ComparesFieldByFieldAndNestedRecordsByTheirOwnHelper() {
    var inner = new IrStructType("Inner", [new IrField("V", IrType.I32, false)]);
    var outer = new IrStructType("Outer", [new IrField("I", IrType.Struct(inner), false), new IrField("N", IrType.I16, false)]);
    var x = new IrLocal("x", IrType.Struct(outer));
    var y = new IrLocal("y", IrType.Struct(outer));
    var module = new IrModule("M", [new IrFunction("F", true, IrType.Bool, [x, y], [], new IrBlock([new IrReturn(new IrStructEquals(new IrLocalRef(x), new IrLocalRef(y)))]))]) { Types = [inner, outer] };
    var text = Emitted(module);
    Assert.That(text, Does.Contain("PlyEq_Outer(x, y)").And.Contain("PlyEq_Inner(a.I, b.I) And a.N = b.N").And.Contain("PlyEq_Inner = a.V = b.V"));
  }

  [Test]
  public void StatementsOfTheIr_AreWritten() {
    var array = new IrLocal("arr", IrType.Array(IrType.I32));
    var global = new IrGlobal("Counter", IrType.I32, I32(0));
    var record = new IrStructType("R", [new IrField("V", IrType.I32, false)]);
    var r = new IrLocal("r", IrType.Struct(record));
    var module = new IrModule("M", [
      new IrFunction("Helper", false, IrType.I32, [_a], [], new IrBlock([new IrReturn(new IrLocalRef(_a))])),
      new IrFunction("F", true, IrType.Void, [_a, array], [r], new IrBlock([
        new IrExprStmt(new IrStep(new IrLocalRef(_a), true, true, false)),
        new IrExprStmt(new IrStep(new IrArrayLoad(new IrLocalRef(array), I32(0)), false, false, true)),
        new IrExprStmt(new IrBinary(IrBinaryOp.Add, new IrLocalRef(_a), I32(1))),
        new IrExprStmt(new IrCall("Helper", IrType.I32, [new IrLocalRef(_a)])),
        new IrExprStmt(new IrArrayLoad(new IrLocalRef(array), I32(1))),
        new IrWriteLine(new IrStrConst("done")),
        new IrGlobalAssign(global, new IrBinary(IrBinaryOp.Add, new IrGlobalRef(global), I32(1))),
        new IrDeclare(r, new IrStructDefault(IrType.Struct(record))),
        new IrFieldStore(new IrLocalRef(r), record.Fields[0], new IrGlobalRef(global)),
        new IrStore(new IrLocalRef(array), new IrGlobalRef(global), new IrFieldLoad(new IrLocalRef(r), record.Fields[0])),
        new IrSwitch(new IrLocalRef(_a), [new IrSwitchSection([1], false, new IrBlock([new IrBlock([new IrBreak()])])), new IrSwitchSection([2], false, new IrBlock([]))])
      ]))
    ]) { Globals = [global], Types = [record] };
    var text = Emitted(module);
    Assert.That(text, Does.Contain("a = PlyAdd32(a, 1)").And.Contain("Helper a").And.Contain("discard = arr(1)").And.Contain("Debug.Print \"done\"")
      .And.Contain("Counter = PlyAdd32(Counter, 1)").And.Contain("arr(Counter) = r2.V").And.Contain("index = 0").And.Contain("PlyZero_R()"));
  }

  [Test]
  public void ExpressionsOfTheIr_AreWritten() {
    var text = Emitted(new IrModule("M", [Function(IrType.Bool, [_a, _b],
      new IrReturn(new IrBinary(IrBinaryOp.NotEqual, new IrBinary(IrBinaryOp.Equal, new IrConst(IrType.Bool, 1), new IrConst(IrType.Bool, 0)),
        new IrIntrinsic(IrIntrinsicOp.StrEquals, IrType.Bool, [new IrStrConst("é"), new IrStrConst("x\u0001y")]))))]));
    Assert.That(text, Does.Contain("ChrW(233)").And.Contain("\"x\" & ChrW(1) & \"y\"").And.Contain("True = False <> ("));
    var floats = Emitted(new IrModule("M", [Function(IrType.F64, [],
      new IrReturn(new IrIntrinsic(IrIntrinsicOp.RoundHalfUp, IrType.F64, [new IrUnary(IrUnaryOp.Negate, new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, 2.5)))])))]));
    Assert.That(floats, Does.Contain("PlyRoundHalfUp(-2.5)"));
    var hex = Emitted(new IrModule("M", [new IrFunction("F", true, IrType.String, [new IrLocal("s", IrType.I8)], [], new IrBlock([
      new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrConcat, IrType.String, [
        new IrIntrinsic(IrIntrinsicOp.FormatRadix, IrType.String, [new IrLocalRef(new IrLocal("s", IrType.I8)), I32(16), I32(1), new IrConst(IrType.Bool, 0), I32((int)RadixMode.TwoComplement)]),
        new IrIntrinsic(IrIntrinsicOp.FormatRadix, IrType.String, [new IrLocalRef(new IrLocal("s", IrType.I8)), I32(16), I32(1), new IrConst(IrType.Bool, 1), I32((int)RadixMode.TwoComplement)])]))]))]));
    Assert.That(hex, Does.Contain("LCase(Hex(CByte(s And 255)))").And.Contain("Hex(CByte(s And 255))"));
  }

  [Test]
  public void ConversionsTheFrontendsDoNotMake_AreWritten() {
    var u8 = new IrLocal("d", IrType.F64);
    Assert.That(Emitted(new IrModule("M", [new IrFunction("F", true, IrType.U8, [u8], [], new IrBlock([new IrReturn(new IrConvert(IrType.U8, new IrLocalRef(u8), IrConversion.Saturate))]))])), Does.Contain("CByte(PlySat(d, 0#, 255#))"));
    Assert.That(Emitted(new IrModule("M", [new IrFunction("F", true, IrType.I8, [u8], [], new IrBlock([new IrReturn(new IrConvert(IrType.I8, new IrLocalRef(u8), IrConversion.Saturate))]))])), Does.Contain("CInt(PlySat(d, -128#, 127#))"));
    var bits = new IrLocal("b", IrType.U64);
    Assert.That(Emitted(new IrModule("M", [new IrFunction("F", true, IrType.I64, [bits], [], new IrBlock([new IrReturn(new IrConvert(IrType.I64, new IrLocalRef(bits)))]))]), VisualBasicProfile.Vba7x64), Does.Contain("F = CLngLng(b)"));
    var word = new IrLocal("w", IrType.I32);
    Assert.That(Emitted(new IrModule("M", [new IrFunction("F", true, IrType.U16, [word], [], new IrBlock([new IrReturn(new IrConvert(IrType.U16, new IrLocalRef(new IrLocal("w", IrType.I32))))]))])), Does.Contain("w And 65535&"));
    var wide = new IrLocal("q", IrType.I64);
    var module = new IrModule("M", [new IrFunction("F", true, IrType.I16, [wide], [], new IrBlock([new IrReturn(new IrConvert(IrType.I16, new IrLocalRef(wide)))]))]);
    Assert.That(Emitted(module, VisualBasicProfile.Vba7x64), Does.Contain("PlyWrap16(CLng(q And 65535^))"));
  }

  [TestCase("foreign")]
  [TestCase("array field")]
  [TestCase("undefined call")]
  [TestCase("rethrow")]
  [TestCase("filter")]
  [TestCase("unmapped catch")]
  [TestCase("break")]
  [TestCase("continue")]
  [TestCase("store")]
  [TestCase("field store")]
  [TestCase("async")]
  [TestCase("bool shift")]
  [TestCase("long op")]
  [TestCase("string constant")]
  public void IrTheBackendCannotWrite_IsRefused(string kind) {
    var record = new IrStructType("R", [new IrField("A", IrType.Array(IrType.I32), false)]);
    var plain = new IrStructType("P", [new IrField("V", IrType.I32, false)]);
    var exception = new IrLocal("e", IrType.Object(IrExceptionClass.Exception));
    IrModule Module(params IrStmt[] body) => new("M", [Function(IrType.Void, [_a], body)]);
    var module = kind switch {
      "foreign" => new IrModule("M", [Function(IrType.Void, [])]) { ForeignFunctions = [new IrForeignFunction("Native", true, "lib", IrType.Void, [])] },
      "array field" => new IrModule("M", [Function(IrType.Void, [])]) { Types = [record] },
      "undefined call" => Module(new IrExprStmt(new IrCall("Missing", IrType.I32, []))),
      "rethrow" => Module(new IrRethrow()),
      "filter" => Module(new IrTry(new IrBlock([]), [new IrCatch(IrExceptionClass.Exception, exception, new IrConst(IrType.Bool, 1), new IrBlock([]))], null)),
      "unmapped catch" => Module(new IrTry(new IrBlock([]), [new IrCatch(IrExceptionClass.KeyNotFoundException, null, null, new IrBlock([]))], null)),
      "break" => Module(new IrBreak()),
      "continue" => Module(new IrContinue()),
      "store" => new IrModule("M", [new IrFunction("G", false, IrType.Array(IrType.I32), [], [], new IrBlock([new IrReturn(new IrNewArray(IrType.Array(IrType.I32), I32(1)))])),
        Function(IrType.Void, [], new IrStore(new IrCall("G", IrType.Array(IrType.I32), []), I32(0), I32(1)))]),
      "field store" => new IrModule("M", [new IrFunction("G", false, IrType.Struct(plain), [], [], new IrBlock([new IrReturn(new IrStructDefault(IrType.Struct(plain)))])),
        Function(IrType.Void, [], new IrFieldStore(new IrCall("G", IrType.Struct(plain), []), plain.Fields[0], I32(1)))]) { Types = [plain] },
      "async" => new IrModule("M", [new IrFunction("F", true, IrType.Async(IrType.I32), [], [], new IrBlock([new IrReturn(I32(1))]))]),
      "bool shift" => Module(new IrExprStmt(new IrBinary(IrBinaryOp.Less, new IrConst(IrType.Bool, 1), new IrConst(IrType.Bool, 0)))),
      "long op" => Module(new IrExprStmt(new IrBinary(IrBinaryOp.ShiftLeft, new IrConst(IrType.I64, 1), I32(1)))),
      _ => new IrModule("M", [Function(IrType.Void, [])], [new IrModuleConst("S", IrType.String, []) { Strings = ["a\tb"] }])
    };
    var problems = Problems(module, kind == "long op" ? VisualBasicProfile.Vba7x64 : null);
    Assert.That(problems, Does.Match("PLY26|PLY0100"), kind);
  }

}
