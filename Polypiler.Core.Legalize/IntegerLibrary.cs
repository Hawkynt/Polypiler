using System;
using System.Collections.Generic;
using Polypiler.Core.IR;

namespace Polypiler.Core.Legalize;

/// <summary>
/// The integer and bit operations of the IR as the helpers of the library compute them
/// (<see cref="HelperOperations"/>): which helper a node of an operation calls for its type, with which
/// further arguments - the width, the bounds of the type - or which other operations of the IR it is
/// where it needs no helper. A backend decides for each node whether its target writes it natively;
/// where it does not, this is what it becomes, the same for every backend.
/// </summary>
public static class IntegerLibrary {

  /// <summary>
  /// A node of an integer operation as the call of its helper of the library, or as other operations
  /// of the IR; <see langword="null"/> for an operation, or a type of it, the library has no helper for.
  /// </summary>
  /// <param name="node">The node, an <see cref="IrIntrinsic"/> whose operands are legalized.</param>
  /// <param name="calls">Makes the calls.</param>
  /// <returns>
  /// An expression of the type of the node that evaluates its arguments once, in their order; other
  /// operations of the IR it is made of are legalized in turn, so that one on 64 bits becomes the call
  /// of its helper where the profile lacks those.
  /// </returns>
  public static IrExpr? Call(IrExpr node, LibraryCalls calls) {
    ArgumentNullException.ThrowIfNull(calls);
    if (node is not IrIntrinsic intrinsic || intrinsic.Arguments.Count == 0)
      return null;

    var arguments = intrinsic.Arguments;
    var type = Operand(intrinsic);
    if (!type.IsInteger)
      return null;

    var name = intrinsic.Op.ToString();
    var bits = type.Bits;
    var wide = bits == 64;
    var signed = type.IsSigned;
    IrExpr Helper(string helper, params IReadOnlyList<IrExpr> values) => calls.Call(helper, intrinsic.Type, values);
    IrExpr Sized(string helper) => wide ? Helper(helper + "64", arguments) : Helper(helper + "32", [.. arguments, Int(bits)]);
    IrExpr ByWidth(string helper) => wide ? Helper(helper + "64", arguments) : bits == 32 ? Helper(helper + "32", arguments) : Helper(helper + "Narrow", [.. arguments, Int(bits)]);
    IrExpr Signedness(string helper) => Helper(helper + (signed ? "Signed" : "Unsigned") + (wide ? "64" : "32"), arguments);

    switch (intrinsic.Op) {
      case IrIntrinsicOp.RotateLeft or IrIntrinsicOp.RotateRight or IrIntrinsicOp.PopCount or IrIntrinsicOp.LeadingZeroCount or IrIntrinsicOp.TrailingZeroCount:
        return ByWidth(name);
      case IrIntrinsicOp.Log2:
        return wide ? Helper("Log2Of64", arguments) : bits == 32 ? Helper("Log2Of32", arguments) : Helper("Log2Narrow", [.. arguments, Int(bits)]);
      case IrIntrinsicOp.Parity:
        return new IrBinary(IrBinaryOp.And, calls.Call(wide ? "PopCount64" : bits == 32 ? "PopCount32" : "PopCountNarrow", IrType.I32,
          wide || bits == 32 ? arguments : [.. arguments, Int(bits)]), Int(1));
      case IrIntrinsicOp.ByteSwap:
        return bits == 8 ? arguments[0] : Helper("ByteSwap" + bits, arguments);
      case IrIntrinsicOp.BitReverse:
        return ByWidth(name);
      case IrIntrinsicOp.IsPow2:
        return Signedness(name);
      case IrIntrinsicOp.RoundUpPow2:
        return wide ? Helper("RoundUpPow2Of64", arguments) : Helper("RoundUpPow2Of32", [.. arguments, Int(bits)]);
      case IrIntrinsicOp.BigMul:
        return calls.Legalize(new IrBinary(IrBinaryOp.Multiply, new IrConvert(intrinsic.Type, arguments[0]), new IrConvert(intrinsic.Type, arguments[1])));
      case IrIntrinsicOp.MulHigh:
        return bits >= 32 ? Signedness(name) : NarrowHigh(intrinsic, type);
      case IrIntrinsicOp.DivFloor or IrIntrinsicOp.ModFloor or IrIntrinsicOp.DivEuclid or IrIntrinsicOp.ModEuclid:
        return Divided(intrinsic, type, Helper, calls);
      case IrIntrinsicOp.Clamp:
        return Signedness(name);
      case IrIntrinsicOp.Abs:
        return !signed ? arguments[0] : wide ? Helper("AbsSigned64", arguments) : Helper("AbsSigned32", [arguments[0], Smallest(type)]);
      case IrIntrinsicOp.AbsWrap:
        return !signed ? arguments[0] : Helper(wide ? "AbsWrap64" : "AbsWrap32", arguments);
      case IrIntrinsicOp.Sign:
        return (signed, wide) switch {
          (true, true) => Helper("Sign64", arguments),
          (false, true) => Helper("SignUnsigned64", arguments),
          (true, false) => Helper("Sign32", arguments),
          _ => new IrConditional(new IrBinary(IrBinaryOp.NotEqual, Widened(arguments[0]), Zero(Widened(arguments[0]).Type)), Int(1), Int(0))
        };
      case IrIntrinsicOp.Min or IrIntrinsicOp.Max:
        return wide ? Helper(name + (signed ? "Signed64" : "Unsigned64"), arguments) : null;
      case IrIntrinsicOp.AddSaturate or IrIntrinsicOp.SubSaturate or IrIntrinsicOp.MulSaturate:
        return Bounded(intrinsic, type, name, Helper);
      case IrIntrinsicOp.AddOverflows or IrIntrinsicOp.SubOverflows or IrIntrinsicOp.MulOverflows:
        return Overflows(intrinsic, type, name, Helper, calls);
      case IrIntrinsicOp.AddCarry or IrIntrinsicOp.SubBorrow:
        var op = intrinsic.Op == IrIntrinsicOp.AddCarry ? IrBinaryOp.Add : IrBinaryOp.Subtract;
        var (left, right) = (Widened(arguments[0]), Widened(arguments[1]));
        return calls.Legalize(Narrowed(new IrBinary(op, new IrBinary(op, left, right), new IrConditional(arguments[2], One(left.Type), Zero(left.Type))), type));
      case IrIntrinsicOp.CarryOut or IrIntrinsicOp.BorrowOut:
        return wide ? Helper(name + "64", arguments) : null;
      case IrIntrinsicOp.ExtractBits or IrIntrinsicOp.InsertBits or IrIntrinsicOp.TestBit or IrIntrinsicOp.SetBit or IrIntrinsicOp.ClearBit or IrIntrinsicOp.ToggleBit:
        return Sized(name);
      case IrIntrinsicOp.LowMask:
        return wide ? Helper("LowMask64", arguments) : Helper("LowMask32", [.. arguments, Int(bits)]);
      case IrIntrinsicOp.IsolateLowestSet or IrIntrinsicOp.ClearLowestSet or IrIntrinsicOp.MaskToLowestSet:
        return Helper(name + (wide ? "64" : "32"), arguments);
      case IrIntrinsicOp.SignExtend or IrIntrinsicOp.ZeroExtend:
        return wide ? Helper(name + "64", arguments) : Helper(name + "32", [.. arguments, Int(bits)]);
      case IrIntrinsicOp.ISqrt:
        return Helper(wide ? "ISqrt64" : "ISqrt32", arguments);
      case IrIntrinsicOp.LoadLittle or IrIntrinsicOp.LoadBig or IrIntrinsicOp.StoreLittle or IrIntrinsicOp.StoreBig when !arguments[0].Type.IsArray:
        // An access to a view is the view's own business.
        return null;
      case IrIntrinsicOp.LoadLittle or IrIntrinsicOp.LoadBig:
        return wide
          ? Helper("Load64", [.. arguments, Truth(intrinsic.Op == IrIntrinsicOp.LoadLittle)])
          : Helper("Load32", [.. arguments, Int(bits / 8), Truth(intrinsic.Op == IrIntrinsicOp.LoadLittle)]);
      case IrIntrinsicOp.StoreLittle or IrIntrinsicOp.StoreBig:
        return wide
          ? Helper("Store64", [.. arguments, Truth(intrinsic.Op == IrIntrinsicOp.StoreLittle)])
          : Helper("Store32", [.. arguments, Int(bits / 8), Truth(intrinsic.Op == IrIntrinsicOp.StoreLittle)]);
      default:
        return null;
    }
  }

  /// <summary>
  /// The type a node's helper is chosen by: that of its first argument, but the type of the node for a
  /// mask and a load, whose first argument is the same for every width, and the type of the value for
  /// a store.
  /// </summary>
  /// <param name="intrinsic">The node.</param>
  public static IrType Operand(IrIntrinsic intrinsic) => intrinsic.Op switch {
    IrIntrinsicOp.LowMask or IrIntrinsicOp.LoadLittle or IrIntrinsicOp.LoadBig => intrinsic.Type,
    IrIntrinsicOp.StoreLittle or IrIntrinsicOp.StoreBig => intrinsic.Arguments[2].Type,
    _ => intrinsic.Arguments[0].Type
  };

  /// <summary>The upper half of a product of a type of 8 or 16 bits: the product in 32 bits, which holds it exactly, shifted down.</summary>
  private static IrExpr NarrowHigh(IrIntrinsic intrinsic, IrType type) {
    var wide = type.IsSigned ? IrType.I32 : IrType.U32;
    var product = new IrBinary(IrBinaryOp.Multiply, new IrConvert(wide, intrinsic.Arguments[0]), new IrConvert(wide, intrinsic.Arguments[1]));
    return new IrConvert(type, new IrBinary(type.IsSigned ? IrBinaryOp.ShiftRight : IrBinaryOp.ShiftRightLogical, product, Int(type.Bits)));
  }

  /// <summary>A division that rounds otherwise: its helper for a signed type, the truncating division for an unsigned one, where both agree.</summary>
  private static IrExpr Divided(IrIntrinsic intrinsic, IrType type, Func<string, IReadOnlyList<IrExpr>, IrExpr> helper, LibraryCalls calls) {
    var arguments = intrinsic.Arguments;
    if (!type.IsSigned)
      return calls.Legalize(Narrowed(new IrBinary(intrinsic.Op is IrIntrinsicOp.DivFloor or IrIntrinsicOp.DivEuclid ? IrBinaryOp.Divide : IrBinaryOp.Remainder, Widened(arguments[0]), Widened(arguments[1])), type));

    var name = intrinsic.Op switch {
      IrIntrinsicOp.DivFloor => "FloorDivide",
      IrIntrinsicOp.ModFloor => "FloorModulo",
      IrIntrinsicOp.DivEuclid => "EuclidDivide",
      _ => "EuclidModulo"
    };
    return type.Bits == 64 ? helper(name + "64", arguments) : helper(name + "32", [arguments[0], arguments[1], Smallest(type)]);
  }

  /// <summary>A saturating operation: the helper of the width, told the bounds of a type of up to 32 bits.</summary>
  private static IrExpr Bounded(IrIntrinsic intrinsic, IrType type, string name, Func<string, IReadOnlyList<IrExpr>, IrExpr> helper) {
    var arguments = intrinsic.Arguments;
    var helperName = name + (type.IsSigned ? "Signed" : "Unsigned") + (type.Bits == 64 ? "64" : "32");
    if (type.Bits == 64)
      return helper(helperName, arguments);

    if (type.IsSigned)
      return helper(helperName, [arguments[0], arguments[1], Smallest(type), Largest(type)]);

    return intrinsic.Op == IrIntrinsicOp.SubSaturate ? helper(helperName, arguments) : helper(helperName, [arguments[0], arguments[1], Largest(type)]);
  }

  /// <summary>An overflow test: the helper of the width, told the bounds of a type of up to 32 bits; an unsigned difference is a comparison.</summary>
  private static IrExpr Overflows(IrIntrinsic intrinsic, IrType type, string name, Func<string, IReadOnlyList<IrExpr>, IrExpr> helper, LibraryCalls calls) {
    var arguments = intrinsic.Arguments;
    if (!type.IsSigned && intrinsic.Op == IrIntrinsicOp.SubOverflows)
      return calls.Legalize(new IrBinary(IrBinaryOp.Less, Widened(arguments[0]), Widened(arguments[1])));

    var helperName = name + (type.IsSigned ? "Signed" : "Unsigned") + (type.Bits == 64 ? "64" : "32");
    if (type.Bits == 64)
      return helper(helperName, arguments);

    return type.IsSigned ? helper(helperName, [arguments[0], arguments[1], Smallest(type), Largest(type)]) : helper(helperName, [arguments[0], arguments[1], Largest(type)]);
  }

  /// <summary>A value of a type narrower than 32 bits as the <c>int</c> or <c>uint</c> the arithmetic of the IR takes; a wider one as it is.</summary>
  private static IrExpr Widened(IrExpr value)
    => value.Type.Bits >= 32 ? value : value is IrConst constant ? new IrConst(value.Type.IsSigned ? IrType.I32 : IrType.U32, constant.Value) : new IrConvert(value.Type.IsSigned ? IrType.I32 : IrType.U32, value);

  /// <summary>A result of the arithmetic of a widened value wrapped back to the narrower type.</summary>
  private static IrExpr Narrowed(IrExpr value, IrType type) => value.Type == type ? value : new IrConvert(type, value);

  private static IrConst Int(int value) => new(IrType.I32, value);

  private static IrConst Truth(bool value) => new(IrType.Bool, value ? 1 : 0);

  private static IrConst Zero(IrType type) => new(type, 0);

  private static IrConst One(IrType type) => new(type, 1);

  private static IrConst Smallest(IrType type) => new(type, (long)IrOperators.Minimum(type));

  private static IrConst Largest(IrType type) => new(type, (long)(ulong)IrOperators.Maximum(type));

}
