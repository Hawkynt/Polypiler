using System;
using Polypiler.Core.IR;

namespace Polypiler.Core.Legalize;

/// <summary>
/// Lowers an <see cref="IrIntegerOp"/> (#118) to what every backend already writes: the operators of
/// the IR where one has the contract - <c>Add[wrap]</c> is <see cref="IrBinaryOp.Add"/>,
/// <c>Add[trap IntegerOverflow]</c> is <see cref="IrBinaryOp.AddChecked"/>, a <c>[mask]</c> shift of 32 or
/// 64 bits is the shift operator - the saturating operations for <c>[sat]</c>, a computation in 32 or
/// 64 bits converted back for a narrow type, and the helpers of <c>Contracts.cs</c> for a zero-divisor
/// or <c>ovf</c> contract no operator has and for a shift whose amount may reach the width. Every
/// operand is evaluated once, in its order. <see cref="LibraryLegalization.Lower"/> applies it before
/// the rewrite of a backend, so that no backend needs to know the node.
/// </summary>
public static class IntegerContractLegalization {

  /// <summary>
  /// A node of an integer operation with its contract as the operators and helpers it is made of;
  /// <see langword="null"/> for any other expression, and for a contract whose trap label no operator
  /// or helper raises (a node then stays, and the backend refuses it).
  /// </summary>
  /// <param name="expression">The expression, its operands legalized.</param>
  /// <param name="calls">Makes the calls of the helpers.</param>
  /// <returns>The replacement, legalized, or <see langword="null"/>.</returns>
  public static IrExpr? Rewrite(IrExpr expression, LibraryCalls calls) {
    ArgumentNullException.ThrowIfNull(calls);
    return expression is IrIntegerOp node ? Lower(node, calls) is { } lowered ? calls.Legalize(lowered) : null : null;
  }

  private static IrExpr? Lower(IrIntegerOp node, LibraryCalls calls) => node.Op switch {
    IrIntegerOpcode.Add or IrIntegerOpcode.Sub or IrIntegerOpcode.Mul or IrIntegerOpcode.Neg => Arithmetic(node),
    IrIntegerOpcode.UDiv or IrIntegerOpcode.URem or IrIntegerOpcode.SDiv or IrIntegerOpcode.SRem => Division(node, calls),
    _ => Shift(node, calls)
  };

  private static IrExpr? Arithmetic(IrIntegerOp node) {
    var type = node.Type;
    var (left, right) = node.Op == IrIntegerOpcode.Neg ? (new IrConst(type, 0), node.Operands[0]) : (node.Operands[0], node.Operands[1]);
    var (op, checkedOp, saturate) = node.Op switch {
      IrIntegerOpcode.Add => (IrBinaryOp.Add, IrBinaryOp.AddChecked, IrIntrinsicOp.AddSaturate),
      IrIntegerOpcode.Mul => (IrBinaryOp.Multiply, IrBinaryOp.MultiplyChecked, IrIntrinsicOp.MulSaturate),
      _ => (IrBinaryOp.Subtract, IrBinaryOp.SubtractChecked, IrIntrinsicOp.SubSaturate)
    };

    var contract = node.Contract.Overflow!;
    return contract.Kind switch {
      IrOverflowKind.Saturate => new IrIntrinsic(saturate, type, [left, right]),
      IrOverflowKind.Trap when contract.Label != IrTrap.IntegerOverflow => null,
      // A narrow type is computed exactly in 64 bits, and the conversion back traps where the result leaves it.
      IrOverflowKind.Trap => type.IsWideInteger ? new IrBinary(checkedOp, left, right)
        : new IrConvert(type, new IrBinary(op, To(IrType.I64, left), To(IrType.I64, right)), IrConversion.Trap),
      // Modulo 2^32 and then modulo 2^width is modulo 2^width.
      _ => type.IsWideInteger ? new IrBinary(op, left, right) : new IrConvert(type, new IrBinary(op, To(IrType.I32, left), To(IrType.I32, right)))
    };
  }

  private static IrExpr? Division(IrIntegerOp node, LibraryCalls calls) {
    var type = node.Type;
    var signed = node.Op is IrIntegerOpcode.SDiv or IrIntegerOpcode.SRem;
    if (type.IsSigned != signed) {
      // The bits are divided as the other signedness reads them.
      var other = Counterpart(type);
      var inner = Division(node with { Type = other, Operands = [To(other, node.Operands[0]), To(other, node.Operands[1])] }, calls);
      return inner is null ? null : new IrConvert(type, inner);
    }

    var (divisor, ovf) = (node.Contract.Divisor!, node.Contract.Overflow);
    if ((divisor.Kind == IrDivisorKind.Trap && divisor.Label != IrTrap.DivideByZero) || ovf is { Kind: IrOverflowKind.Trap, Label: not IrTrap.IntegerOverflow })
      return null;

    var (dividend, by) = (node.Operands[0], node.Operands[1]);
    var quotient = node.Op is IrIntegerOpcode.UDiv or IrIntegerOpcode.SDiv;
    var zero = divisor.Kind == IrDivisorKind.Zero;
    var wrap = ovf is { Kind: IrOverflowKind.Wrap };
    if (!signed && !zero)
      // An unsigned narrow type is divided as the int it widens to, which no quotient leaves.
      return type.IsWideInteger ? new IrBinary(quotient ? IrBinaryOp.Divide : IrBinaryOp.Remainder, dividend, by)
        : new IrConvert(type, new IrBinary(quotient ? IrBinaryOp.Divide : IrBinaryOp.Remainder, To(IrType.I32, dividend), To(IrType.I32, by)));

    if (!signed)
      return calls.Call((quotient ? "DivideOrZeroUnsigned" : "RemainderOrZeroUnsigned") + (type.Bits == 64 ? "64" : "32"), type, dividend, by);

    if (!zero && !wrap && type.IsWideInteger)
      return new IrBinary(quotient ? IrBinaryOp.Divide : IrBinaryOp.Remainder, dividend, by);

    var name = quotient ? "DivideContract" : "RemainderContract";
    return type.Bits == 64
      ? calls.Call(name + "64", type, dividend, by, Truth(zero), Truth(wrap))
      : calls.Call(name + "32", type, dividend, by, Int((int)IrOperators.Minimum(type)), Truth(zero), Truth(wrap));
  }

  private static IrExpr? Shift(IrIntegerOp node, LibraryCalls calls) {
    var type = node.Type;
    var contract = node.Contract.Shift!;
    var (value, amount) = (node.Operands[0], node.Operands[1]);
    var unsigned = type.IsSigned ? Counterpart(type) : type;
    var signed = type.IsSigned ? type : Counterpart(type);
    if (contract.Kind == IrShiftKind.Mask)
      return Masked(node.Op, type, value, amount, unsigned, signed);

    int? trap = contract.Kind switch {
      IrShiftKind.Unbounded => -1,
      IrShiftKind.Strict => (int)IrTrap.InvalidOperation,
      _ => contract.Label is IrTrap.DivideByZero or IrTrap.IntegerOverflow or IrTrap.InvalidOperation ? (int)contract.Label : null
    };
    if (trap is not { } code)
      return null;

    var count = To(unsigned, amount);
    var (name, operand) = node.Op switch {
      IrIntegerOpcode.Shl => ("ShiftLeftBounded", value),
      IrIntegerOpcode.LShr => ("ShiftRightLogicalBounded", To(unsigned, value)),
      _ => ("ShiftRightArithmeticBounded", To(signed, value))
    };
    return type.Bits == 64
      ? calls.Call(name + "64", type, operand, count, Int(code))
      : calls.Call(name + "32", type, operand, count, Int(type.Bits), Int(code));
  }

  /// <summary>A <c>[mask]</c> shift: the shift operators of 32 and 64 bits mask the amount themselves; a narrow type masks it and shifts as an <c>int</c>.</summary>
  private static IrExpr Masked(IrIntegerOpcode op, IrType type, IrExpr value, IrExpr amount, IrType unsigned, IrType signed) {
    if (type.IsWideInteger) {
      var count = To(IrType.I32, amount);
      return op switch {
        IrIntegerOpcode.Shl => new IrBinary(IrBinaryOp.ShiftLeft, value, count),
        IrIntegerOpcode.LShr => new IrBinary(IrBinaryOp.ShiftRightLogical, value, count),
        _ => type.IsSigned ? new IrBinary(IrBinaryOp.ShiftRight, value, count) : new IrConvert(type, new IrBinary(IrBinaryOp.ShiftRight, To(signed, value), count))
      };
    }

    var masked = new IrBinary(IrBinaryOp.And, To(IrType.I32, amount), Int(type.Bits - 1));
    var widened = op switch {
      IrIntegerOpcode.Shl => To(IrType.I32, value),
      IrIntegerOpcode.LShr => To(IrType.I32, To(unsigned, value)),
      _ => To(IrType.I32, To(signed, value))
    };
    return new IrConvert(type, new IrBinary(op == IrIntegerOpcode.Shl ? IrBinaryOp.ShiftLeft : IrBinaryOp.ShiftRight, widened, masked));
  }

  /// <summary>The integer type of the same width and the other signedness.</summary>
  private static IrType Counterpart(IrType type) => type.Kind switch {
    IrTypeKind.I8 => IrType.U8,
    IrTypeKind.U8 => IrType.I8,
    IrTypeKind.I16 => IrType.U16,
    IrTypeKind.U16 => IrType.I16,
    IrTypeKind.I32 => IrType.U32,
    IrTypeKind.U32 => IrType.I32,
    IrTypeKind.I64 => IrType.U64,
    _ => IrType.I64
  };

  /// <summary>A value converted to a type, wrapping; the value itself where it has the type.</summary>
  private static IrExpr To(IrType type, IrExpr value) => value.Type == type ? value : new IrConvert(type, value);

  private static IrConst Int(int value) => new(IrType.I32, value);

  private static IrConst Truth(bool value) => new(IrType.Bool, value ? 1 : 0);

}
