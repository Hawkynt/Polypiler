using System;
using System.Globalization;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.CSharp;

[TestFixture]
public sealed class CSharpOrderedOutputTests {

  [Test, Category("Slow")]
  public void EveryProfileCompilesTheStringOverloadForNulls() {
    var module = Nulls();
    foreach (var profile in CSharpProfile.All) {
      Assert.That(CSharpEmitter.Check(module, profile), Is.Empty, profile.Name);
      var text = CSharpEmitter.Emit(module, profile).Single().Content;
      Assert.That(text, Does.Contain("global::System.Console.WriteLine((string)null)"), profile.Name);
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
    }
  }

  [Test, NonParallelizable]
  public void NullLiteralNullLocalAndNullCallEachWriteOneBlankLine() {
    var text = CSharpEmitter.Emit(Nulls()).Single().Content;
    var generated = GeneratedCSharp.Compile(text, checkOverflow: true);
    Assert.That(Capture(() => generated.Method("Run").Invoke(null, [true])),
      Is.EqualTo((42, new string('~', 5))));
  }

  [Test, NonParallelizable]
  public void LoopWritesStayOrderedAndEvaluateEachOperandOnce() {
    var counter = new IrGlobal("counter", IrType.I32, Int(0));
    var count = new IrGlobalRef(counter);
    var next = new IrCall("Next", IrType.String, []);
    var body = new IrBlock([new IrWriteLine(next)]);
    var module = new IrModule("LoopOutput", [
      new("Run", true, IrType.I32, [], [], new([
        new IrWriteLine(new IrStrConst("first")),
        new IrWhile(new IrBinary(IrBinaryOp.Less, count, Int(2)), body),
        new IrReturn(count)
      ])),
      new("Next", false, IrType.String, [], [], new([
        new IrGlobalAssign(counter, new IrBinary(IrBinaryOp.Add, count, Int(1))),
        new IrReturn(new IrStrConst("next"))
      ]))
    ]) { Globals = [counter] };
    var generated = GeneratedCSharp.Compile(CSharpEmitter.Emit(module).Single().Content, checkOverflow: true);
    Assert.That(Capture(() => generated.Method("Run").Invoke(null, null)), Is.EqualTo((2, "first~next~next~")));
  }

  [Test]
  public void TheCapabilityCheckTraversesTheOutputOperand() {
    var value = new IrConditional(new IrBinary(IrBinaryOp.Equal,
      new IrIntrinsic(IrIntrinsicOp.PopCount, IrType.I32, [new IrConst(IrType.U32, 1)]), Int(1)),
      new IrStrConst("one"), new IrStrConst("other"));
    var module = new IrModule("Unknown", [new("Run", true, IrType.Void, [], [], new([new IrWriteLine(value), new IrReturn(null)]))]);
    var noCapabilities = new CSharpCapabilities([]);
    var messages = CSharpEmitter.Check(module, CSharpProfile.Default, noCapabilities);
    Assert.That(messages.Select(message => (message.Code, message.Message)),
      Is.EqualTo(new[] { (CSharpEmitter.Unsupported, "'PopCount' is not supported by the C# backend yet") }));
  }

  private static IrModule Nulls() {
    var choose = new IrLocal("choose", IrType.Bool);
    var missing = new IrNull(IrType.String);
    var local = new IrLocal("text", IrType.String);
    return new("NullOutput", [
      new("Run", true, IrType.I32, [choose], [local], new([
        new IrDeclare(local, missing),
        new IrWriteLine(new IrStrConst("")),
        new IrWriteLine(missing),
        new IrWriteLine(new IrLocalRef(local)),
        new IrWriteLine(new IrCall("Missing", IrType.String, [])),
        new IrWriteLine(new IrConditional(new IrLocalRef(choose), missing, missing)),
        new IrReturn(Int(42))
      ])),
      new("Missing", false, IrType.String, [], [], new([new IrReturn(missing)]))
    ]);
  }

  private static IrConst Int(int value) => new(IrType.I32, value);

  private static (object? Value, string Text) Capture(Func<object?> run) {
    using var output = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "~" };
    var previous = Console.Out;
    try {
      Console.SetOut(output);
      var value = run();
      return (value, output.ToString());
    } finally {
      Console.SetOut(previous);
    }
  }

}
