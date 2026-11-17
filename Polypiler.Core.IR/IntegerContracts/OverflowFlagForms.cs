using System;
using System.Numerics;

namespace Polypiler.Core.IR;

/// <summary>
/// The wrapped-value-plus-flag forms <c>AddOverflow</c>, <c>SubOverflow</c> and <c>MulOverflow</c> (#122) of
/// Rust's <c>overflowing_add</c> and C's <c>__builtin_add_overflow</c>: the <c>[wrap]</c> result and whether
/// the <c>[trap]</c> condition holds, on every integer type. Until the IR has tuples (#120) the pair is two
/// expressions over the same operands - an <see cref="IrIntegerOp"/> under <c>[wrap]</c> and the
/// <see cref="IrIntrinsicOp.AddOverflows"/> test - so the operands must be ones that may be read twice.
/// </summary>
public static class OverflowFlagForms {

  /// <summary>The value and the flag of a flag form, as two expressions.</summary>
  /// <param name="op"><see cref="IrIntegerOpcode.Add"/>, <see cref="IrIntegerOpcode.Sub"/> or <see cref="IrIntegerOpcode.Mul"/>.</param>
  /// <param name="left">The first operand: a constant or a variable, which reading twice does not change.</param>
  /// <param name="right">The second operand, of the same integer type and kind.</param>
  /// <returns>The wrapped result, of the operands' type, and the overflow flag, a <c>bool</c>.</returns>
  /// <exception cref="ArgumentException">The opcode has no flag form, an operand may not be read twice, or the operands are not of one integer type.</exception>
  public static (IrExpr Value, IrExpr Overflow) Of(IrIntegerOpcode op, IrExpr left, IrExpr right) {
    ArgumentNullException.ThrowIfNull(left);
    ArgumentNullException.ThrowIfNull(right);
    var test = op switch {
      IrIntegerOpcode.Add => IrIntrinsicOp.AddOverflows,
      IrIntegerOpcode.Sub => IrIntrinsicOp.SubOverflows,
      IrIntegerOpcode.Mul => IrIntrinsicOp.MulOverflows,
      _ => throw new ArgumentException($"{op} has no flag form.", nameof(op))
    };
    if (left is not (IrConst or IrLocalRef) || right is not (IrConst or IrLocalRef))
      throw new ArgumentException("A flag form reads its operands twice; each must be a constant or a variable.", nameof(left));

    if (!IrIntegerContracts.IsInteger(left.Type) || right.Type != left.Type)
      throw new ArgumentException($"A flag form takes two operands of one integer type, not {left.Type} and {right.Type}.", nameof(right));

    return (new IrIntegerOp(op, left.Type, [left, right], IrOverflowContract.Wrap), new IrIntrinsic(test, IrType.Bool, [left, right]));
  }

  /// <summary>The value and the flag of a flag form for two values, as the interpreter carries them.</summary>
  /// <param name="op"><see cref="IrIntegerOpcode.Add"/>, <see cref="IrIntegerOpcode.Sub"/> or <see cref="IrIntegerOpcode.Mul"/>.</param>
  /// <param name="type">An integer type.</param>
  /// <param name="left">The first value.</param>
  /// <param name="right">The second value.</param>
  /// <returns>The <c>[wrap]</c> result and whether the exact result lies outside the type.</returns>
  /// <exception cref="ArgumentException">The opcode has no flag form.</exception>
  public static (long Value, bool Overflow) Evaluate(IrIntegerOpcode op, IrType type, long left, long right) {
    ArgumentNullException.ThrowIfNull(type);
    var (a, b) = ((BigInteger)IrOperators.Number(type, left), (BigInteger)IrOperators.Number(type, right));
    var (exact, wrapped) = op switch {
      IrIntegerOpcode.Add => (a + b, left + right),
      IrIntegerOpcode.Sub => (a - b, left - right),
      IrIntegerOpcode.Mul => (a * b, unchecked(left * right)),
      _ => throw new ArgumentException($"{op} has no flag form.", nameof(op))
    };
    return (IrOperators.Wrap(type, wrapped), exact < (BigInteger)IrOperators.Minimum(type) || exact > (BigInteger)IrOperators.Maximum(type));
  }

}
