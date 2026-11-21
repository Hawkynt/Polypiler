using System;
using System.Collections.Generic;
using System.Linq;
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

  /// <summary>
  /// The whole doubles of <c>a % d</c>, <c>a + b</c>, <c>a - b</c> and <c>a * b</c> of whole doubles and
  /// integers, or <see langword="null"/>. Of two integers below 2^53 the result is an exact integer by
  /// construction, never a NaN (#1416), which a place JSDoc states an integer type of takes with a check.
  /// The range of a sum, a difference and a product is widened to the next power of two, so that a loop
  /// that counts reaches its bound in as many steps as there are powers, not values.
  /// </summary>
  private static JsType? WholeResult(Operator op, JsType a, JsType b, Node right) {
    if (!(a.IsWhole || a.IsInteger) || !(b.IsWhole || b.IsInteger) || a.IsAbsent || b.IsAbsent || a.Enum is not null || b.Enum is not null)
      return null;

    var nan = a.MayBeNaN || b.MayBeNaN;
    switch (op) {
      case Operator.Remainder when right is NumericLiteral { Value: var divisor } && divisor == Math.Truncate(divisor) && divisor != 0 && Math.Abs(divisor) <= (double)_exact:
        var largest = (Int128)Math.Abs(divisor) - 1;
        return JsType.Whole(a.Low >= 0 ? 0 : -largest, a.High <= 0 ? 0 : largest, nan);
      // By a computed divisor, which may be 0: a NaN as well, else below the largest divisor.
      case Operator.Remainder:
        var below = IsExact(b) ? Int128.Max(Int128.Abs(b.Low), Int128.Abs(b.High)) : Int128.MaxValue;
        // A divisor that is only ever 0 makes a NaN alone: a double.
        return below == 0 ? null
          : below == Int128.MaxValue ? JsType.Whole(Int128.MinValue, Int128.MaxValue)
          : JsType.Whole(a.Low >= 0 ? 0 : -below, a.High <= 0 ? 0 : below);
      // Past 2^53 a double still holds an integer, the one JavaScript computes: of no bound.
      case Operator.Addition or Operator.Subtraction or Operator.Multiplication when !IsExact(a) || !IsExact(b):
        return JsType.Whole(Int128.MinValue, Int128.MaxValue, nan);
      case Operator.Addition or Operator.Subtraction or Operator.Multiplication:
        Int128[] corners = op switch {
          Operator.Addition => [a.Low + b.Low, a.High + b.High],
          Operator.Subtraction => [a.Low - b.High, a.High - b.Low],
          _ => [a.Low * b.Low, a.Low * b.High, a.High * b.Low, a.High * b.High]
        };
        var (low, high) = (Below(corners.Min()), Above(corners.Max()));
        return low >= -_exact && high <= _exact ? JsType.Whole(low, high, nan) : JsType.Whole(Int128.MinValue, Int128.MaxValue, nan);
      default:
        return null;
    }
  }

  /// <summary>
  /// <c>Math.min</c> and <c>Math.max</c> of whole doubles and integers, one at least, and <c>Math.abs</c> of
  /// a whole double of a bounded range: whole doubles of the range they select, a NaN where an argument
  /// may be one. Of integers alone <see cref="IntegerCall"/> decides first; anything else is <see langword="null"/>.
  /// </summary>
  private JsType? WholeCall(string function, CallExpression call) {
    var arguments = call.Arguments.Select(argument => argument is SpreadElement ? JsType.Unknown : this.Of(argument)).ToList();
    if (arguments.Count == 0 || !arguments.TrueForAll(type => (type.IsWhole || type.IsInteger) && !type.IsAbsent && type.Enum is null) || !arguments.Exists(type => type.IsWhole))
      return null;

    var nan = arguments.Exists(type => type.MayBeNaN);
    switch (function) {
      case "Math.min":
        return JsType.Whole(arguments.Min(type => type.Low), arguments.Min(type => type.High), nan);
      case "Math.max":
        return JsType.Whole(arguments.Max(type => type.Low), arguments.Max(type => type.High), nan);
      default:
        if (arguments is not [var value])
          return null;

        if (!IsExact(value))
          return JsType.Whole(0, Int128.MaxValue, nan);

        return JsType.Whole(value.Low >= 0 ? value.Low : value.High <= 0 ? -value.High : 0, Int128.Max(-value.Low, value.High), nan);
    }
  }

  /// <summary>
  /// The whole doubles a rounding of <c>a / d</c> yields, where <c>a</c> is a whole double or an integer of
  /// a range and <c>d</c> a positive integer literal (#1416): the range of <c>a</c> divided, rounded
  /// outwards - <c>Math.floor(remaining / 256)</c> of a count that is never negative is never negative -
  /// a NaN where <c>a</c> may be one; <see langword="null"/> for any other argument.
  /// </summary>
  private JsType? Quotient(Node rounded) {
    if (rounded is not BinaryExpression { Operator: Operator.Division, Right: NumericLiteral { Value: var divisor } } quotient || divisor != Math.Truncate(divisor) || divisor is < 1 or > 9007199254740992
        || this.Of(quotient.Left) is not { IsAbsent: false, Enum: null } dividend || !(dividend.IsWhole || dividend.IsInteger))
      return null;

    var by = (Int128)divisor;
    Int128 Down(Int128 bound) => bound == Int128.MinValue ? bound : bound >= 0 ? bound / by : -((-bound + by - 1) / by);
    Int128 Up(Int128 bound) => bound == Int128.MaxValue ? bound : bound <= 0 ? -(-bound / by) : (bound + by - 1) / by;
    return JsType.Whole(Down(dividend.Low), Up(dividend.High), dividend.MayBeNaN);
  }

  /// <summary>A lower bound widened: 0 for one that is not negative, else the next <c>-2^k</c> of at least 8 bits below it.</summary>
  private static Int128 Below(Int128 bound) {
    Int128 power = 256;
    while (-power > bound)
      power *= 2;

    return bound >= 0 ? 0 : -power;
  }

  /// <summary>An upper bound widened: -1 for a negative one, else the next <c>2^k - 1</c> of at least 8 bits above it.</summary>
  private static Int128 Above(Int128 bound) {
    Int128 power = 256;
    while (power - 1 < bound)
      power *= 2;

    return bound < 0 ? -1 : power - 1;
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
    var taken = JsType.Whole(Int128.Max(whole.Low, low), Int128.Min(whole.High, high), whole.MayBeNaN);
    var left = below ? JsType.Whole(Int128.Max(whole.Low, high + 1), whole.High, whole.MayBeNaN) : JsType.Whole(whole.Low, Int128.Min(whole.High, low - 1), whole.MayBeNaN);
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
