using Acornima;
using Acornima.Ast;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>
/// A quotient or remainder of integers under a coercion as JavaScript's quotient of doubles becomes
/// (#1367): 0 for a divisor of 0, and the one quotient that does not fit wraps, as <c>ToInt32</c> of the
/// double does. Nothing is tested where the divisor is a constant other than 0 and -1. A dividend with
/// an effect keeps the trap, and the caveat <c>js-div-zero</c> announces it.
/// </summary>
internal sealed partial class Lowering {

  /// <summary>Whether an expression can be computed twice for once: a variable or a constant, converted or not.</summary>
  private static bool Pure(IrExpr value) => value is IrLocalRef or IrConst || value is IrConvert { Operand: var operand } && Pure(operand);

  /// <summary>
  /// A quotient or remainder of 32-bit integers under <c>ToInt32</c> or <c>ToUint32</c>: 0 for a divisor
  /// of 0, which JavaScript divides into an infinity or a NaN, and for a signed divisor of -1 the
  /// negated dividend modulo 2^32 or 0, which is what the double gives for the smallest value as well.
  /// A divisor the inference bounds away from 0, and from -1 or a dividend bounded away from the
  /// smallest value, is not tested for it. <see langword="null"/> where a test is needed and the
  /// dividend has an effect, which keeps the trap: the divisor is tested before the dividend would be
  /// evaluated.
  /// </summary>
  private IrExpr? Quotient(Operator op, IrExpr dividend, IrExpr divisor, Node left, Node right) {
    var type = dividend.Type;
    var divide = op == Operator.Division ? IrBinaryOp.Divide : IrBinaryOp.Remainder;
    var (low, high) = (types.Of(right).Low, types.Of(right).High);
    var zero = low <= 0 && high >= 0;
    var overflow = type.IsSigned && low <= -1 && high >= -1 && types.Of(left).Low <= int.MinValue;
    if (!zero && !overflow)
      return new IrBinary(divide, dividend, divisor);

    if (Contains(left, node => node is CallExpression or NewExpression or UpdateExpression or AssignmentExpression))
      return null;

    // The divisor is evaluated once, in the first test; the branches read the variable or constant it is then.
    var (tested, again) = Pure(divisor) ? (divisor, divisor) : this.Bound(divisor);
    var nothing = new IrConst(type, 0);
    IrExpr quotient = new IrBinary(divide, dividend, again);
    if (overflow) {
      IrExpr negated = divide == IrBinaryOp.Divide ? new IrBinary(IrBinaryOp.Subtract, nothing, dividend) : nothing;
      quotient = new IrConditional(new IrBinary(IrBinaryOp.Equal, zero ? again : tested, new IrConst(type, -1)), negated, quotient);
    }

    return zero ? new IrConditional(new IrBinary(IrBinaryOp.Equal, tested, nothing), nothing, quotient) : quotient;
  }

}
