using System;
using System.Collections.Generic;
using Acornima;
using Acornima.Ast;

namespace Polypiler.Core.Frontends.JavaScript.Inference;

/// <summary>
/// Whole doubles (#1413): a double whose every value is a NaN or an integer of a range, which a place of
/// integers JSDoc states takes with a check. <c>Math.trunc</c>, <c>floor</c>, <c>ceil</c> and
/// <c>round</c> of a number make one of no bound - an infinity among its values - and <c>x % d</c> by an
/// integer literal bounds it below <c>|d|</c>, of the sign of <c>x</c>; <c>+</c> and <c>-</c> with an
/// integer shift the range. The branches of <c>x &lt; c ? … : …</c> - and of <c>&lt;=</c>, <c>&gt;</c>,
/// <c>&gt;=</c> - read a constant <c>x</c> of such a range as the part the test leaves them, the NaN in
/// the branch a failed test takes: <c>reduced &lt; 0 ? reduced + 256 : reduced</c> of
/// <c>Math.trunc(v) % 256</c> is a NaN or an integer from 0 to 255.
/// </summary>
internal sealed partial class TypeInference {

  /// <summary>The ranges the branch being typed reads a variable as, which its test proves.</summary>
  private readonly Dictionary<Variable, JsType> _narrowed = [];

  /// <summary>Whether a range is bounded so that doubles hold every integer of it exactly.</summary>
  private static bool IsExact(JsType type) => type.Low >= -_exact && type.High <= _exact;

  /// <summary>The whole doubles of <c>a % d</c>, <c>a + b</c> and <c>a - b</c> where one operand is whole and the other an integer, or <see langword="null"/>.</summary>
  private static JsType? WholeResult(Operator op, JsType a, JsType b, Node right) {
    if (!(a.IsWhole || b.IsWhole) || !(a.IsWhole || a.IsInteger) || !(b.IsWhole || b.IsInteger) || a.IsAbsent || b.IsAbsent)
      return null;

    switch (op) {
      case Operator.Remainder when right is NumericLiteral { Value: var divisor } && divisor == Math.Truncate(divisor) && divisor != 0 && Math.Abs(divisor) <= (double)_exact:
        var largest = (Int128)Math.Abs(divisor) - 1;
        return JsType.Whole(a.Low >= 0 ? 0 : -largest, a.High <= 0 ? 0 : largest);
      case Operator.Addition or Operator.Subtraction when IsExact(a) && IsExact(b):
        var sum = op == Operator.Addition ? JsType.Whole(a.Low + b.Low, a.High + b.High) : JsType.Whole(a.Low - b.High, a.High - b.Low);
        return IsExact(sum) ? sum : null;
      default:
        return null;
    }
  }

  /// <summary>
  /// The join of the branches of a conditional, each typed with what the test proves of a constant of
  /// whole doubles that it compares with an integer literal; a test of any other form proves nothing.
  /// </summary>
  private JsType Narrowed(ConditionalExpression choice, Func<Node, JsType> branch) {
    if (choice.Test is not BinaryExpression { Operator: Operator.LessThan or Operator.LessThanOrEqual or Operator.GreaterThan or Operator.GreaterThanOrEqual, Left: Identifier name, Right: NumericLiteral { Value: var limit } } test
        || limit != Math.Truncate(limit) || Math.Abs(limit) > (double)_exact
        || this._scope!.Bindings.GetValueOrDefault(name) is not Variable { Type: { IsWhole: true, IsAbsent: false } whole } variable || !this.IsFixed(variable))
      return JsType.Join(branch(choice.Consequent), branch(choice.Alternate));

    // The values the test takes: below a bound, or from one on.
    var bound = (Int128)limit;
    var (low, high) = test.Operator switch {
      Operator.LessThan => (whole.Low, bound - 1),
      Operator.LessThanOrEqual => (whole.Low, bound),
      Operator.GreaterThan => (bound + 1, whole.High),
      _ => (bound, whole.High)
    };
    var below = test.Operator is Operator.LessThan or Operator.LessThanOrEqual;
    var taken = JsType.Whole(Int128.Max(whole.Low, low), Int128.Min(whole.High, high));
    var left = below ? JsType.Whole(Int128.Max(whole.Low, high + 1), whole.High) : JsType.Whole(whole.Low, Int128.Min(whole.High, low - 1));
    if (taken.Low > taken.High || left.Low > left.High)
      return JsType.Join(branch(choice.Consequent), branch(choice.Alternate));

    this._narrowed[variable] = taken;
    var consequent = branch(choice.Consequent);
    this._narrowed[variable] = left;
    var alternate = branch(choice.Alternate);
    this._narrowed.Remove(variable);
    return JsType.Join(consequent, alternate);
  }

}
