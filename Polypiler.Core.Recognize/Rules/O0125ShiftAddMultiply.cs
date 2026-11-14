using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize.Rules;

/// <summary>
/// O0125: a sum or difference of shifted copies of one value - <c>(x &lt;&lt; 3) + (x &lt;&lt; 1)</c>,
/// <c>(x &lt;&lt; 4) - x</c>, chains of them - becomes a multiplication by the constant they add up to.
/// <para>
/// Addition, subtraction, negation and shifts to the left are exact on weighted bits modulo the
/// width (<see cref="WeightEvaluator"/>), so the sum is <c>c · x</c> exactly when every bit of the one
/// atom has the weight <c>c · 2^j</c> - for a narrower signed atom, which is sign-extended, the top
/// bit the weight <c>-c · 2^j</c>. Only wrapping arithmetic takes part: an operation that traps is an
/// atom of its own and the rule does not apply. A factor of zero, one, a power of two or minus one
/// is left as written, and so is an expression without an addition or subtraction. The value is
/// evaluated once instead of several times and must have no effect and not trap.
/// </para>
/// </summary>
public sealed class O0125ShiftAddMultiply : ExpressionRule {

  /// <summary>Creates the rule.</summary>
  public O0125ShiftAddMultiply() : base("O0125", "multiplication by a constant from shifts and adds") { }

  /// <inheritdoc/>
  protected override IrExpr? Recognize(IrExpr expression, RuleContext context) {
    if (expression is not IrBinary { Op: IrBinaryOp.Add or IrBinaryOp.Subtract, Type.IsWideInteger: true } || WeightEvaluator.Evaluate(expression, context) is not { } value
      || value.Single is not { } atom || value.Image.Constant != 0 || !atom.Type.IsInteger || !IrShape.IsTrapFree(atom))
      return null;

    var image = value.Image;
    var width = atom.Type.Bits;
    var key = image.Terms.Keys.First() & ~63;
    var factor = image.Terms.GetValueOrDefault(key);
    if ((factor & (factor - 1)) == 0 || factor == image.Mask)
      return null;

    for (var bit = 0; bit < width; ++bit) {
      var expected = (factor << bit) & image.Mask;
      if (atom.Type.IsSigned && bit == width - 1 && width < image.Width)
        expected = (0 - expected) & image.Mask;

      if (image.Terms.GetValueOrDefault(key + bit) != expected)
        return null;
    }

    var constant = Images.Constant(expression.Type, (long)factor);
    return new IrBinary(IrBinaryOp.Multiply, Images.Fit(atom, expression.Type), constant);
  }

}
