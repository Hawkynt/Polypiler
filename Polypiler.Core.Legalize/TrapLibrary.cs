using System;
using Polypiler.Core.IR;

namespace Polypiler.Core.Legalize;

/// <summary>
/// The traps of integer arithmetic as the helpers of the library (<see cref="HelperOperations"/>) write
/// them, for a target whose numbers do not trap: a check that throws the exception class of the IR's
/// trap, and the operation on exact numbers. The division and the remainder of up to 32 bits, the
/// checked sum, difference, product and negation, the checked conversion between integers, and the
/// conversion of a floating-point number to an integer of up to 32 bits, which saturates or traps.
/// Once the helper is inlined where an operand is a constant, folding removes the check it proves.
/// A checked step of a variable whose value is used is the call of a lambda that stores the checked
/// sum, as the IR has no assignment that is an expression; a step of an array element is the target's.
/// </summary>
public static class TrapLibrary {

  /// <summary>A node of a trapping integer operation as the call of its helper; <see langword="null"/> for any other node.</summary>
  /// <param name="node">The node, its operands legalized.</param>
  /// <param name="calls">Makes the calls.</param>
  /// <returns>An expression of the type of the node that evaluates its operands once, in their order.</returns>
  public static IrExpr? Call(IrExpr node, LibraryCalls calls) {
    ArgumentNullException.ThrowIfNull(calls);
    return node switch {
      IrBinary { Op: IrBinaryOp.Divide or IrBinaryOp.Remainder } binary when IsNumber(binary.Left.Type) => Divided(binary, calls),
      IrBinary binary when IrOperators.Traps(binary.Op) && binary.Left.Type.IsInteger => Checked(binary.Op, binary.Left, binary.Right, calls),
      IrUnary { Op: IrUnaryOp.NegateChecked } negation when negation.Type.IsInteger
        => Checked(IrBinaryOp.SubtractChecked, new IrConst(negation.Type, 0), negation.Operand, calls),
      IrConvert { Contract: IrConversion.Trap } conversion when conversion.Type.IsInteger && conversion.Operand.Type.IsInteger && !conversion.Type.IsEnum && !conversion.Operand.Type.IsEnum
        => Range(conversion, calls),
      IrConvert conversion when conversion.Operand.Type.IsFloat && IsNumber(conversion.Type) => FromFloat(conversion, calls),
      IrStep { Trap: true, Target: not IrArrayLoad { Array.Type.IsArray: true } } step when step.Type.IsInteger
        => WideIntegerLibrary.Stepped(step, before => Checked(step.Increment ? IrBinaryOp.AddChecked : IrBinaryOp.SubtractChecked, before, new IrConst(step.Type, 1), calls)),
      _ => null
    };
  }

  /// <summary>Whether a type is an integer of up to 32 bits, a number of the target.</summary>
  private static bool IsNumber(IrType type) => type.IsInteger && !type.IsLong && !type.IsEnum;

  /// <summary>The quotient or remainder of up to 32 bits: of a signed type on <c>int</c>, told its smallest value, of an unsigned one on <c>uint</c>.</summary>
  private static IrExpr Divided(IrBinary binary, LibraryCalls calls) {
    var type = binary.Left.Type;
    var name = binary.Op == IrBinaryOp.Divide ? "Divide" : "Remainder";
    return type.IsSigned
      ? calls.Call(name + "32", type, binary.Left, binary.Right, new IrConst(IrType.I32, (long)IrOperators.Minimum(type)))
      : calls.Call(name + "Unsigned32", type, binary.Left, binary.Right);
  }

  /// <summary>A checked sum, difference or product: of up to 32 bits told the bounds of the type, of 64 bits the helper of its signedness.</summary>
  private static IrExpr Checked(IrBinaryOp op, IrExpr left, IrExpr right, LibraryCalls calls) {
    var type = left.Type;
    var name = op switch {
      IrBinaryOp.AddChecked => "AddChecked",
      IrBinaryOp.SubtractChecked => "SubtractChecked",
      _ => "MultiplyChecked"
    };
    if (type.IsLong)
      return calls.Call(name + (type.IsSigned ? "Signed64" : "Unsigned64"), type, left, right);

    return type.IsSigned
      ? calls.Call(name + "32", type, left, right, Bound(IrType.I32, IrOperators.Minimum(type)), Bound(IrType.I32, IrOperators.Maximum(type)))
      : calls.Call(name + "Unsigned32", type, left, right, Bound(IrType.U32, IrOperators.Maximum(type)));
  }

  /// <summary>
  /// A checked conversion between integers: the value checked against the bounds of the target as far
  /// as the source reaches them, in the type of the source widened to 32 or 64 bits, then converted,
  /// which changes nothing the check let through.
  /// </summary>
  internal static IrExpr Range(IrConvert conversion, LibraryCalls calls) {
    var (from, to) = (conversion.Operand.Type, conversion.Type);
    var (low, high) = (Int128.Max(IrOperators.Minimum(to), IrOperators.Minimum(from)), Int128.Min(IrOperators.Maximum(to), IrOperators.Maximum(from)));
    var (name, work) = (from.IsLong, from.IsSigned || from.Bits < 32) switch {
      (true, _) => (from.IsSigned ? "CheckRangeSigned64" : "CheckRangeUnsigned64", from),
      (false, true) => ("CheckRange32", IrType.I32),
      _ => ("CheckRangeUnsigned32", IrType.U32)
    };
    return new IrConvert(to, calls.Call(name, work, conversion.Operand, Bound(work, low), Bound(work, high)));
  }

  /// <summary>A floating-point number converted to an integer of up to 32 bits: saturated to the bounds of the type, or checked against them.</summary>
  private static IrExpr FromFloat(IrConvert conversion, LibraryCalls calls) {
    var type = conversion.Type;
    var (low, high) = (IrOperators.Minimum(type), IrOperators.Maximum(type));
    if (conversion.Contract == IrConversion.Trap)
      return type == IrType.U32
        ? calls.Call("FromDoubleCheckedUnsigned32", type, conversion.Operand, Bound(IrType.U32, high))
        : calls.Call("FromDoubleChecked32", type, conversion.Operand, Bound(IrType.I32, low), Bound(IrType.I32, high));

    return type == IrType.U32
      ? calls.Call("SaturateToUInt32", type, conversion.Operand)
      : calls.Call("SaturateToInt32", type, conversion.Operand, Bound(IrType.I32, low), Bound(IrType.I32, high));
  }

  private static IrConst Bound(IrType type, Int128 value) => new(type, IrOperators.Wrap(type, (long)(ulong)(UInt128)(value & ulong.MaxValue)));

}
