using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.CSharp;

/// <summary>A call of a floating-point member of the base library, as the frontend hands it to the table.</summary>
/// <param name="Arguments">The arguments, lowered.</param>
/// <param name="Constants">The value of every argument the compiler folds to a constant, otherwise <see langword="null"/>.</param>
/// <param name="Parameters">The type of every parameter of the overload the compiler chose, as it displays them.</param>
/// <param name="Result">The type of the call.</param>
public sealed record CSharpFloatCall(IReadOnlyList<IrExpr> Arguments, IReadOnlyList<object?> Constants, IReadOnlyList<string> Parameters, IrType Result);

/// <summary>
/// One member of <c>System.Math</c>, <c>System.MathF</c>, <c>double</c> or <c>float</c> and the IR it
/// means. A member whose .NET 10 implementation is a fixed sequence of other operations (<c>double.Exp2</c>
/// is <c>Math.Pow(2, x)</c>, <c>double.ExpM1</c> is <c>Math.Exp(x) - 1</c>) becomes that sequence, so a
/// translated program computes what the source computed.
/// </summary>
/// <param name="Type">The type as the compiler displays it: <c>System.Math</c>, <c>System.MathF</c>, <c>double</c> or <c>float</c>.</param>
/// <param name="Name">The name of the member.</param>
/// <param name="Arity">The number of arguments.</param>
/// <param name="Operations">The operations of the IR the call becomes, in the order they appear.</param>
/// <param name="Lower">The IR of a call, or <see langword="null"/> when the call is outside the subset (a mode that is not a constant).</param>
public sealed record CSharpFloatMember(string Type, string Name, int Arity, IReadOnlyList<IrIntrinsicOp> Operations, Func<CSharpFloatCall, IrExpr?> Lower) {

  /// <summary>The member as a diagnostic names it.</summary>
  public override string ToString() => $"{this.Type}.{this.Name}/{this.Arity}";

}

/// <summary>
/// The table of the floating-point members of the C# base library the frontend translates, read by
/// the frontend and by the tests. A member that is not in it is refused by its name.
/// </summary>
public static class CSharpFloatApi {

  private static readonly string[] _math = ["System.Math", "System.MathF"];
  private static readonly string[] _number = ["double", "float"];
  private static readonly string[] _all = [.. _math, .. _number];

  /// <summary>Every member, by type, name and arity.</summary>
  public static IReadOnlyList<CSharpFloatMember> Members { get; } = Build();

  private static readonly Dictionary<(string, string, int), CSharpFloatMember> _byKey = Members.ToDictionary(member => (member.Type, member.Name, member.Arity));

  /// <summary>The member of a type with a name and an arity, or <see langword="null"/>.</summary>
  /// <param name="type">The type as the compiler displays it.</param>
  /// <param name="name">The name of the member.</param>
  /// <param name="arity">The number of arguments.</param>
  /// <returns>The entry.</returns>
  public static CSharpFloatMember? Find(string type, string name, int arity) => _byKey.GetValueOrDefault((type, name, arity));

  private static List<CSharpFloatMember> Build() {
    var members = new List<CSharpFloatMember>();
    void Add(string[] types, string name, int arity, IrIntrinsicOp[] operations, Func<CSharpFloatCall, IrExpr?> lower) {
      foreach (var type in types)
        members.Add(new CSharpFloatMember(type, name, arity, operations, lower));
    }

    void Op(string[] types, string name, int arity, IrIntrinsicOp op) => Add(types, name, arity, [op], call => Intrinsic(op, call.Result, call.Arguments));

    // One operation of the IR each, in both formats.
    foreach (var (name, op) in new[] {
      ("Sin", IrIntrinsicOp.Sin), ("Cos", IrIntrinsicOp.Cos), ("Tan", IrIntrinsicOp.Tan),
      ("Asin", IrIntrinsicOp.Asin), ("Acos", IrIntrinsicOp.Acos), ("Atan", IrIntrinsicOp.Atan),
      ("Sinh", IrIntrinsicOp.Sinh), ("Cosh", IrIntrinsicOp.Cosh), ("Tanh", IrIntrinsicOp.Tanh),
      ("Asinh", IrIntrinsicOp.Asinh), ("Acosh", IrIntrinsicOp.Acosh), ("Atanh", IrIntrinsicOp.Atanh),
      ("Exp", IrIntrinsicOp.Exp), ("Log", IrIntrinsicOp.Log), ("Log2", IrIntrinsicOp.Log2), ("Log10", IrIntrinsicOp.Log10),
      ("Cbrt", IrIntrinsicOp.Cbrt), ("BitIncrement", IrIntrinsicOp.BitIncrement), ("BitDecrement", IrIntrinsicOp.BitDecrement),
      ("ILogB", IrIntrinsicOp.ILogB)
    })
      Op(_all, name, 1, op);

    foreach (var (name, op) in new[] {
      ("Atan2", IrIntrinsicOp.Atan2), ("Pow", IrIntrinsicOp.Pow), ("Log", IrIntrinsicOp.LogBase), ("CopySign", IrIntrinsicOp.CopySign),
      ("MinMagnitude", IrIntrinsicOp.MinMagnitude), ("MaxMagnitude", IrIntrinsicOp.MaxMagnitude), ("ScaleB", IrIntrinsicOp.ScaleB)
    })
      Op(_all, name, 2, op);

    foreach (var (name, op) in new[] {
      ("Abs", IrIntrinsicOp.Abs), ("Sqrt", IrIntrinsicOp.Sqrt), ("Floor", IrIntrinsicOp.Floor), ("Ceiling", IrIntrinsicOp.Ceiling),
      ("Truncate", IrIntrinsicOp.Truncate), ("Round", IrIntrinsicOp.RoundEven), ("Sign", IrIntrinsicOp.Sign)
    })
      Op(_number, name, 1, op);

    Op(_number, "Min", 2, IrIntrinsicOp.Min);
    Op(_number, "Max", 2, IrIntrinsicOp.Max);
    Op(_all, "FusedMultiplyAdd", 3, IrIntrinsicOp.FusedMultiplyAdd);
    Op(["System.Math", .. _number], "Clamp", 3, IrIntrinsicOp.Clamp);
    Op(_math, "IEEERemainder", 2, IrIntrinsicOp.IeeeRemainder);
    Op(_number, "Ieee754Remainder", 2, IrIntrinsicOp.IeeeRemainder);
    Op(_number, "Hypot", 2, IrIntrinsicOp.Hypot);
    Op(_number, "MinNumber", 2, IrIntrinsicOp.MinNumber);
    Op(_number, "MaxNumber", 2, IrIntrinsicOp.MaxNumber);
    Op(_number, "DegreesToRadians", 1, IrIntrinsicOp.DegreesToRadians);
    Op(_number, "RadiansToDegrees", 1, IrIntrinsicOp.RadiansToDegrees);
    foreach (var (name, op) in new[] {
      ("IsNaN", IrIntrinsicOp.IsNaN), ("IsInfinity", IrIntrinsicOp.IsInfinity), ("IsFinite", IrIntrinsicOp.IsFinite),
      ("IsNegative", IrIntrinsicOp.IsNegative), ("IsNormal", IrIntrinsicOp.IsNormal), ("IsSubnormal", IrIntrinsicOp.IsSubnormal),
      ("IsInteger", IrIntrinsicOp.IsInteger)
    })
      Op(_number, name, 1, op);

    // The members of double and float that .NET 10 defines as a sequence of other operations.
    Add(_number, "Exp2", 1, [IrIntrinsicOp.Pow], call => Intrinsic(IrIntrinsicOp.Pow, call.Result, [Number(call.Result, 2), call.Arguments[0]]));
    Add(_number, "Exp10", 1, [IrIntrinsicOp.Pow], call => Intrinsic(IrIntrinsicOp.Pow, call.Result, [Number(call.Result, 10), call.Arguments[0]]));
    Add(_number, "ExpM1", 1, [IrIntrinsicOp.Exp], call => Minus(Intrinsic(IrIntrinsicOp.Exp, call.Result, call.Arguments), Number(call.Result, 1)));
    Add(_number, "Exp2M1", 1, [IrIntrinsicOp.Pow], call => Minus(Intrinsic(IrIntrinsicOp.Pow, call.Result, [Number(call.Result, 2), call.Arguments[0]]), Number(call.Result, 1)));
    Add(_number, "Exp10M1", 1, [IrIntrinsicOp.Pow], call => Minus(Intrinsic(IrIntrinsicOp.Pow, call.Result, [Number(call.Result, 10), call.Arguments[0]]), Number(call.Result, 1)));
    Add(_number, "LogP1", 1, [IrIntrinsicOp.Log], call => Intrinsic(IrIntrinsicOp.Log, call.Result, [Plus(call.Arguments[0], Number(call.Result, 1))]));
    Add(_number, "Log2P1", 1, [IrIntrinsicOp.Log2], call => Intrinsic(IrIntrinsicOp.Log2, call.Result, [Plus(call.Arguments[0], Number(call.Result, 1))]));
    Add(_number, "Log10P1", 1, [IrIntrinsicOp.Log10], call => Intrinsic(IrIntrinsicOp.Log10, call.Result, [Plus(call.Arguments[0], Number(call.Result, 1))]));
    Add(_number, "AsinPi", 1, [IrIntrinsicOp.Asin], call => OverPi(Intrinsic(IrIntrinsicOp.Asin, call.Result, call.Arguments)));
    Add(_number, "AcosPi", 1, [IrIntrinsicOp.Acos], call => OverPi(Intrinsic(IrIntrinsicOp.Acos, call.Result, call.Arguments)));
    Add(_number, "AtanPi", 1, [IrIntrinsicOp.Atan], call => OverPi(Intrinsic(IrIntrinsicOp.Atan, call.Result, call.Arguments)));
    Add(_number, "Atan2Pi", 2, [IrIntrinsicOp.Atan2], call => OverPi(Intrinsic(IrIntrinsicOp.Atan2, call.Result, call.Arguments)));
    Add(_number, "IsPositiveInfinity", 1, [], call => Compare(IrBinaryOp.Equal, call.Arguments[0], double.PositiveInfinity));
    Add(_number, "IsNegativeInfinity", 1, [], call => Compare(IrBinaryOp.Equal, call.Arguments[0], double.NegativeInfinity));
    Add(_number, "IsPositive", 1, [IrIntrinsicOp.IsNegative], call
      => new IrUnary(IrUnaryOp.LogicalNot, Intrinsic(IrIntrinsicOp.IsNegative, IrType.Bool, call.Arguments)));

    // Lerp is MultiplyAddEstimate(a, 1 - t, b * t), which is fused where the processor has the
    // instruction: on every x64 and Arm64 host .NET 10 runs on today.
    Add(_number, "Lerp", 3, [IrIntrinsicOp.FusedMultiplyAdd], call => Reused(call, [0, 2]) ? Intrinsic(IrIntrinsicOp.FusedMultiplyAdd, call.Result, [
      call.Arguments[0],
      Minus(Number(call.Result, 1), call.Arguments[2]),
      new IrBinary(IrBinaryOp.Multiply, call.Arguments[1], call.Arguments[2])
    ]) : null);

    // RootN is a sequence of Sqrt, Cbrt or Pow in .NET 10, with special cases for zero and the infinities.
    Add(_number, "RootN", 2, [IrIntrinsicOp.Sqrt, IrIntrinsicOp.Cbrt, IrIntrinsicOp.Pow, IrIntrinsicOp.CopySign], RootN);

    // Rounding: every mode, given as a constant, and to digits.
    Add(_math, "Round", 2, [IrIntrinsicOp.RoundEven, IrIntrinsicOp.RoundAway, IrIntrinsicOp.Truncate, IrIntrinsicOp.Floor, IrIntrinsicOp.Ceiling, IrIntrinsicOp.RoundDigits], RoundTwo);
    Add(_number, "Round", 2, [IrIntrinsicOp.RoundEven, IrIntrinsicOp.RoundAway, IrIntrinsicOp.Truncate, IrIntrinsicOp.Floor, IrIntrinsicOp.Ceiling, IrIntrinsicOp.RoundDigits], RoundTwo);
    Add(_all, "Round", 3, [IrIntrinsicOp.RoundDigits], call => call.Constants[2] is int mode and >= 0 and <= 4
      ? Intrinsic(IrIntrinsicOp.RoundDigits, call.Result, [call.Arguments[0], call.Arguments[1], new IrConst(IrType.I32, mode)])
      : null);
    return members;
  }

  /// <summary><c>Round(x, mode)</c> by the mode, which has to be a constant, or <c>Round(x, digits)</c> to even.</summary>
  private static IrExpr? RoundTwo(CSharpFloatCall call)
    => call.Parameters[1].EndsWith("MidpointRounding", StringComparison.Ordinal)
      ? ByMode(call)
      : Intrinsic(IrIntrinsicOp.RoundDigits, call.Result, [call.Arguments[0], call.Arguments[1], new IrConst(IrType.I32, 0)]);

  private static IrExpr? ByMode(CSharpFloatCall call) {
    var op = call.Constants[1] switch {
      (int)MidpointRounding.ToEven => IrIntrinsicOp.RoundEven,
      (int)MidpointRounding.AwayFromZero => IrIntrinsicOp.RoundAway,
      (int)MidpointRounding.ToZero => IrIntrinsicOp.Truncate,
      (int)MidpointRounding.ToNegativeInfinity => IrIntrinsicOp.Floor,
      (int)MidpointRounding.ToPositiveInfinity => IrIntrinsicOp.Ceiling,
      _ => (IrIntrinsicOp?)null
    };
    return op is null ? null : Intrinsic(op.Value, call.Result, [call.Arguments[0]]);
  }

  /// <summary>
  /// .NET 10's <c>RootN</c>: <c>Sqrt</c> for 2 (a zero becomes +0), <c>Cbrt</c> for 3, <c>NaN</c> for
  /// 0, and otherwise <c>CopySign(Pow(|x|, 1.0 / n), x)</c> for a finite non-zero x that is positive
  /// or has an odd root, with its own results for zero, the infinities and a NaN. The operands are read
  /// more than once, so they have to be variables or constants.
  /// </summary>
  private static IrExpr? RootN(CSharpFloatCall call) {
    if (!Reused(call, [0, 1]))
      return null;

    var (x, n, type) = (call.Arguments[0], call.Arguments[1], call.Result);
    IrExpr Is(long value) => new IrBinary(IrBinaryOp.Equal, n, new IrConst(IrType.I32, value));
    IrExpr Odd() => new IrBinary(IrBinaryOp.NotEqual, new IrBinary(IrBinaryOp.And, n, new IrConst(IrType.I32, 1)), new IrConst(IrType.I32, 0));
    IrExpr Positive() => new IrBinary(IrBinaryOp.Greater, n, new IrConst(IrType.I32, 0));
    IrExpr If(IrExpr condition, IrExpr then, IrExpr otherwise) => new IrConditional(condition, then, otherwise);
    var nan = Number(type, double.NaN);
    var root = Intrinsic(IrIntrinsicOp.CopySign, type, [
      Intrinsic(IrIntrinsicOp.Pow, type, [Intrinsic(IrIntrinsicOp.Abs, type, [x]), new IrBinary(IrBinaryOp.Divide, Number(type, 1), new IrConvert(type, n))]),
      x
    ]);
    var finite = If(Compare(IrBinaryOp.NotEqual, x, 0),
      If(new IrBinary(IrBinaryOp.LogicalOr, Compare(IrBinaryOp.Greater, x, 0), Odd()), root, nan),
      If(Odd(), Intrinsic(IrIntrinsicOp.CopySign, type, [If(Positive(), Number(type, 0), Number(type, double.PositiveInfinity)), x]),
        If(Positive(), Number(type, 0), Number(type, double.PositiveInfinity))));
    var infinite = If(Compare(IrBinaryOp.Greater, x, 0),
      If(Positive(), Number(type, double.PositiveInfinity), Number(type, 0)),
      If(Odd(), If(Positive(), Number(type, double.NegativeInfinity), Number(type, -0.0)), nan));
    var general = If(Intrinsic(IrIntrinsicOp.IsFinite, IrType.Bool, [x]), finite,
      If(Intrinsic(IrIntrinsicOp.IsNaN, IrType.Bool, [x]), nan, infinite));
    return If(Is(2), If(Compare(IrBinaryOp.NotEqual, x, 0), Intrinsic(IrIntrinsicOp.Sqrt, type, [x]), Number(type, 0)),
      If(Is(3), Intrinsic(IrIntrinsicOp.Cbrt, type, [x]),
        If(Is(0), nan, general)));
  }

  /// <summary>Whether the arguments at the positions, which the lowering reads more than once, can be read twice: variables and constants.</summary>
  private static bool Reused(CSharpFloatCall call, int[] positions)
    => positions.All(position => call.Arguments[position] is IrLocalRef or IrConst);

  private static IrIntrinsic Intrinsic(IrIntrinsicOp op, IrType type, IReadOnlyList<IrExpr> arguments) => new(op, type, arguments);

  private static IrConst Number(IrType type, double value) => new(type, IrOperators.Bits(type, value));

  private static IrBinary Minus(IrExpr left, IrExpr right) => new(IrBinaryOp.Subtract, left, right);

  private static IrBinary Plus(IrExpr left, IrExpr right) => new(IrBinaryOp.Add, left, right);

  private static IrBinary OverPi(IrExpr value) => new(IrBinaryOp.Divide, value, Number(value.Type, Math.PI));

  private static IrBinary Compare(IrBinaryOp op, IrExpr value, double constant) => new(op, value, Number(value.Type, constant));

}
