using System.Collections.Generic;
using System.IO;
using Polypiler.Core.IR;

namespace Polypiler.Prototype.Testing;

/// <summary>The independent C# oracle and corresponding typed IR for ordered standard output.</summary>
internal static class OutputEffectFixture {

  /// <summary>The source that Roslyn compiles for the independent output oracle.</summary>
  public static string Source() => File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "batch", "OutputEffect.cs"));

  /// <summary>The typed statements equivalent to the pinned C# source.</summary>
  public static IrModule Module() {
    var choose = new IrLocal("choose", IrType.Bool);
    var divisor = new IrLocal("divisor", IrType.I32);
    var text = new IrLocal("text", IrType.String);
    var emitText = new IrLocal("divisor", IrType.I32);
    return new("OutputEffect", [
      Function("Run", true, IrType.I32, [choose], First(),
        new IrIf(new IrLocalRef(choose), new IrBlock([Emit(new IrStrConst(""))]), null),
        Emit(new IrStrConst("second")), new IrReturn(Int(42))),
      Function("Trap", true, IrType.I32, [divisor], First(),
        Emit(new IrCall("Text", IrType.String, [new IrLocalRef(divisor)])), new IrReturn(Int(42))),
      Function("Emit", false, IrType.Void, [text], new IrWriteLine(new IrLocalRef(text)), new IrReturn(null)),
      Function("Text", false, IrType.String, [emitText], new IrReturn(new IrConditional(
        new IrBinary(IrBinaryOp.NotEqual, new IrBinary(IrBinaryOp.Divide, Int(42), new IrLocalRef(emitText)), Int(0)),
        new IrStrConst("done"), new IrStrConst("zero"))))
    ]);
  }

  private static IrWriteLine First() => new(new IrStrConst("first"));

  private static IrExprStmt Emit(IrExpr text) => new(new IrCall("Emit", IrType.Void, [text]));

  private static IrConst Int(int value) => new(IrType.I32, value);

  private static IrFunction Function(string name, bool exported, IrType result, IReadOnlyList<IrLocal> parameters, params IrStmt[] body)
    => new(name, exported, result, parameters, [], new IrBlock(body));

}
