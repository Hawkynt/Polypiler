using System;
using System.Collections.Generic;
using Polypiler.Core.IR;

namespace Polypiler.Backends.Testing;

/// <summary>
/// Functions whose IR declares a local while another one of its name is visible, which the IR allows
/// and the frontends do not write yet (#1343). Each is a module <c>Scopes</c> of one public function
/// <c>F</c>, with the calls it is checked with and what the interpreter returns for them.
/// </summary>
internal static class ShadowingModules {

  /// <summary>A function of the set.</summary>
  /// <param name="Name">The class of input the function stands for.</param>
  /// <param name="Module">The module.</param>
  /// <param name="Calls">Arguments of <c>F</c> and the value the interpreter returns for each, as text.</param>
  internal sealed record Case(string Name, IrModule Module, IReadOnlyList<(int Argument, string Result)> Calls) {

    /// <summary>The case as a test name shows it.</summary>
    public override string ToString() => this.Name;

  }

  private static IrConst Int(long value) => new(IrType.I32, value);

  private static IrConst Double(double value) => new(IrType.F64, BitConverter.DoubleToInt64Bits(value));

  private static IrLocalRef Ref(IrLocal local) => new(local);

  private static IrBinary Op(IrBinaryOp op, IrExpr left, IrExpr right) => new(op, left, right);

  private static IrConvert ToDouble(IrExpr value) => new(IrType.F64, value);

  private static IrModule Module(IrType returns, IrLocal[] parameters, IrLocal[] locals, params IrStmt[] body)
    => new("Scopes", [new IrFunction("F", true, returns, parameters, locals, new IrBlock(body))]);

  /// <summary>
  /// The reproduction: <c>t: f64 = 0.5</c>, and in a block <c>t': i32 = n * 2</c> and
  /// <c>return t' + (t + t)</c>, which reads both.
  /// </summary>
  public static Case InnerAfterOuter() {
    var n = new IrLocal("n", IrType.I32);
    var outer = new IrLocal("t", IrType.F64);
    var inner = new IrLocal("t", IrType.I32);
    var module = Module(IrType.F64, [n], [outer, inner],
      new IrDeclare(outer, Double(0.5)),
      new IrBlock([
        new IrDeclare(inner, Op(IrBinaryOp.Multiply, Ref(n), Int(2))),
        new IrReturn(Op(IrBinaryOp.Add, ToDouble(Ref(inner)), Op(IrBinaryOp.Add, Ref(outer), Ref(outer))))
      ]));
    return new("inner after outer", module, [(3, "7"), (0, "1"), (-1, "-1")]);
  }

  /// <summary>
  /// A block reads the outer <c>t</c> before it declares a <c>t</c> of its own, which a <c>let</c> of
  /// the same name would put in its temporal dead zone.
  /// </summary>
  public static Case OuterReadBeforeInner() {
    var n = new IrLocal("n", IrType.I32);
    var outer = new IrLocal("t", IrType.F64);
    var sum = new IrLocal("s", IrType.F64);
    var inner = new IrLocal("t", IrType.I32);
    var module = Module(IrType.F64, [n], [outer, sum, inner],
      new IrDeclare(outer, Double(0.5)),
      new IrDeclare(sum, Double(0)),
      new IrBlock([
        new IrAssign(sum, Op(IrBinaryOp.Add, Ref(sum), Ref(outer))),
        new IrDeclare(inner, Op(IrBinaryOp.Multiply, Ref(n), Int(2))),
        new IrAssign(sum, Op(IrBinaryOp.Add, Ref(sum), ToDouble(Ref(inner))))
      ]),
      new IrReturn(Op(IrBinaryOp.Add, Ref(sum), Ref(outer))));
    return new("outer read before inner", module, [(3, "7"), (0, "1")]);
  }

  /// <summary>A block declares a local named like the parameter and reads both.</summary>
  public static Case ParameterShadowed() {
    var n = new IrLocal("n", IrType.I32);
    var inner = new IrLocal("n", IrType.I64);
    var module = Module(IrType.I32, [n], [inner],
      new IrIf(Op(IrBinaryOp.Greater, Ref(n), Int(0)), new IrBlock([
        new IrDeclare(inner, Op(IrBinaryOp.Multiply, new IrConvert(IrType.I64, Ref(n)), new IrConst(IrType.I64, 3))),
        new IrReturn(new IrConvert(IrType.I32, Op(IrBinaryOp.Add, Ref(inner), new IrConvert(IrType.I64, Ref(n))), IrConversion.Wrap))
      ]), null),
      new IrReturn(Ref(n)));
    return new("parameter shadowed", module, [(2, "8"), (-1, "-1")]);
  }

  /// <summary>The body of a loop declares a local named like the loop variable, from it.</summary>
  public static Case LoopVariableShadowed() {
    var n = new IrLocal("n", IrType.I32);
    var sum = new IrLocal("s", IrType.F64);
    var i = new IrLocal("i", IrType.I32);
    var inner = new IrLocal("i", IrType.F64);
    var module = Module(IrType.F64, [n], [sum, i, inner],
      new IrDeclare(sum, Double(0)),
      new IrDeclare(i, Int(0)),
      new IrFor(Op(IrBinaryOp.Less, Ref(i), Ref(n)), new IrBlock([
        new IrDeclare(inner, Op(IrBinaryOp.Multiply, ToDouble(Ref(i)), Double(0.5))),
        new IrAssign(sum, Op(IrBinaryOp.Add, Ref(sum), Op(IrBinaryOp.Add, Ref(inner), ToDouble(Ref(i)))))
      ]), new IrBlock([new IrAssign(i, Op(IrBinaryOp.Add, Ref(i), Int(1)))])),
      new IrReturn(Ref(sum)));
    return new("loop variable shadowed", module, [(4, "9"), (0, "0")]);
  }

  /// <summary>Boundary: <c>t_1</c> is the name of another local, so the inner <c>t</c> takes the next suffix.</summary>
  public static Case SuffixTaken() {
    var n = new IrLocal("n", IrType.I32);
    var outer = new IrLocal("t", IrType.F64);
    var taken = new IrLocal("t_1", IrType.I32);
    var inner = new IrLocal("t", IrType.I32);
    var module = Module(IrType.F64, [n], [outer, taken, inner],
      new IrDeclare(outer, Double(2)),
      new IrDeclare(taken, Int(1)),
      new IrBlock([
        new IrDeclare(inner, Op(IrBinaryOp.Multiply, Ref(n), Int(2))),
        new IrReturn(Op(IrBinaryOp.Add, ToDouble(Op(IrBinaryOp.Add, Ref(inner), Ref(taken))), Ref(outer)))
      ]));
    return new("suffix taken", module, [(3, "9")]);
  }

  /// <summary>Two sibling blocks each declare a <c>t</c>; neither sees the other.</summary>
  public static Case Siblings() {
    var n = new IrLocal("n", IrType.I32);
    var sum = new IrLocal("s", IrType.F64);
    var first = new IrLocal("t", IrType.I32);
    var second = new IrLocal("t", IrType.F64);
    var module = Module(IrType.F64, [n], [sum, first, second],
      new IrDeclare(sum, Double(0)),
      new IrBlock([new IrDeclare(first, Ref(n)), new IrAssign(sum, Op(IrBinaryOp.Add, Ref(sum), ToDouble(Ref(first))))]),
      new IrBlock([
        new IrDeclare(second, Op(IrBinaryOp.Multiply, ToDouble(Ref(n)), Double(2))),
        new IrAssign(sum, Op(IrBinaryOp.Add, Ref(sum), Ref(second)))
      ]),
      new IrReturn(Ref(sum)));
    return new("siblings", module, [(3, "9"), (-2, "-6")]);
  }

  /// <summary>Every case of the set.</summary>
  public static IReadOnlyList<Case> All { get; } = [InnerAfterOuter(), OuterReadBeforeInner(), ParameterShadowed(), LoopVariableShadowed(), SuffixTaken(), Siblings()];

}
