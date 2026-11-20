using System.Globalization;
using System.Numerics;
using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize.Rules;

/// <summary>
/// O0126: a division or remainder by a constant that was written as a multiplication with a reciprocal
/// becomes the division or remainder again.
/// <list type="bullet">
/// <item><em>unsigned</em>: <c>MulHigh(x, M) &gt;&gt; s</c>, the same high product written
/// <c>(uint)(((ulong)x * M) &gt;&gt; j)</c>, and a narrow operand multiplied in a wider type,
/// <c>((uint)x16 * M) &gt;&gt; k</c>;</item>
/// <item><em>add-back</em>: <c>(t + ((x - t) &gt;&gt; 1)) &gt;&gt; s</c> with <c>t</c> the high product, whose
/// effective multiplier is <c>2^w + M</c>;</item>
/// <item><em>signed</em>: the arithmetic high product shifted, <c>q - (x &gt;&gt; (w - 1))</c> or
/// <c>q + (x &gt;&gt;&gt; (w - 1))</c>, a truncating division;</item>
/// <item><em>remainder</em>: <c>x - q * d</c> with <c>q</c> one of the quotients above of the same
/// <c>x</c> by the same <c>d</c>.</item>
/// </list>
/// <para>
/// The divisor is derived from the multiplier and the total shift, never looked up: with the effective
/// multiplier <c>M</c>, the total shift <c>k</c> and the largest operand <c>X</c> (from its type),
/// <c>d</c> is the integer nearest to <c>2^k / M</c> and <c>e = M·d - 2^k</c>. An unsigned quotient is
/// exact on <c>0..X</c> when <c>e ≥ 0</c>, <c>e·x' &lt; 2^k</c> for the largest <c>x' ≤ X</c> with
/// remainder <c>d - 1</c>, and <c>e·X &lt; 2^k·(d - X mod d)</c>; a signed one when <c>0 &lt; e</c> and
/// <c>e·2^(w-1) ≤ 2^k</c> (Granlund and Montgomery; Warren, <i>Hacker's Delight</i>, ch. 10). The
/// certificate is computed in exact integers and is the proof (<c>validated-rule</c>): the rule is
/// validated exhaustively at 16 bits by the tests, while a solver run on a 32- or 64-bit division does
/// not finish within the budget, and a 64-bit high product would need 128 bits. A multiplication in a
/// type the full product may not fit in is refused. Refusals are reported only for a multiplier near a
/// reciprocal (<c>|e| ≤ d</c>) and not for a bare high product, which is a step towards a quotient;
/// anything else is silent.
/// </para>
/// </summary>
public sealed class O0126ReciprocalDivision : ExpressionRule {

  private const string NotExact = "reciprocal is not exact for the operand range";

  /// <summary>Creates the rule.</summary>
  public O0126ReciprocalDivision() : base("O0126", "division or remainder by a constant from a reciprocal multiplication") { }

  /// <summary>
  /// A matched reciprocal quotient: the dividend as the multiplication reads it, the effective multiplier,
  /// the total shift, whether it is signed, its form, and why the multiplication may wrap if it may.
  /// </summary>
  private sealed record Quotient(IrExpr Value, BigInteger Multiplier, int Shift, bool Signed, string Form, bool Wraps);

  /// <summary>
  /// What the certificate says: the divisor and its excess, or a refusal; <c>Meant</c> is whether the multiplier
  /// is a reciprocal at all - within one step of <c>2^k / d</c> - so that a refusal is worth reporting.
  /// </summary>
  private sealed record Certificate(BigInteger Divisor, BigInteger Excess, string? Refusal, bool Meant);

  /// <inheritdoc/>
  protected override IrExpr? Recognize(IrExpr expression, RuleContext context) {
    if (!expression.Type.IsInteger)
      return null;

    if (expression is IrBinary { Op: IrBinaryOp.Subtract } difference && Images.Peel(difference.Right) is IrBinary { Op: IrBinaryOp.Multiply } product)
      return Remainder(expression, difference.Left, product, context);

    var root = expression is IrConvert { Contract: IrConversion.Wrap, Operand.Type.IsInteger: true } narrowing && narrowing.Type.Bits < narrowing.Operand.Type.Bits
      ? narrowing.Operand : expression;
    if (Find(root, context) is { } found) {
      // A truncation of the quotient keeps it only when every quotient fits the narrower type.
      return Dividend(found.Value).Type.Bits <= expression.Type.Bits ? Apply(expression, found, null, context) : null;
    }

    // An arithmetic high product shifted, without the correction that makes it truncate.
    if (Signed(expression, context) is { } uncorrected && Certify(uncorrected) is { Refusal: null })
      context.Note?.Invoke("O0126 not applied: missing sign correction");
    return null;
  }

  /// <summary>Checks the certificate of a quotient and builds its division, or the remainder by a divisor.</summary>
  private IrExpr? Apply(IrExpr expression, Quotient quotient, BigInteger? remainderOf, RuleContext context) {
    var certificate = Certify(quotient);
    if (certificate.Refusal is { } refusal) {
      if (certificate.Meant)
        context.Note?.Invoke($"O0126 not applied: {refusal}");
      return null;
    }

    if (remainderOf is { } multiplied && multiplied != certificate.Divisor) {
      context.Note?.Invoke("O0126 not applied: remainder uses another divisor");
      return null;
    }

    if (!IrShape.IsTrapFree(expression)) {
      context.Note?.Invoke("O0126 not applied: operand may trap or have effects");
      return null;
    }

    var dividend = Dividend(quotient.Value);
    var op = remainderOf is null ? IrBinaryOp.Divide : IrBinaryOp.Remainder;
    var after = Images.Fit(new IrBinary(op, dividend, Images.Constant(dividend.Type, (long)certificate.Divisor)), expression.Type);
    var name = (quotient.Signed ? "S" : "U") + (remainderOf is null ? "Div" : "Rem");
    var multiplier = quotient.Multiplier.ToString("X", CultureInfo.InvariantCulture).TrimStart('0');
    return context.Certify(this.Id, after, string.Create(CultureInfo.InvariantCulture,
      $"{name}.{dividend.Type} by {certificate.Divisor} (M=0x{multiplier}, shift={quotient.Shift}, e={certificate.Excess}) from {quotient.Form}"), ProofClass);
  }

  /// <summary>
  /// <c>x - q * d</c>: the remainder when <c>q</c> is a reciprocal quotient of the same <c>x</c> by the same <c>d</c>, or
  /// the division <c>x / d</c> that this rule - or the source - already wrote. A target that prefers the remainder
  /// from the quotient (O0127) keeps the second spelling.
  /// </summary>
  private IrExpr? Remainder(IrExpr expression, IrExpr dividend, IrBinary product, RuleContext context) {
    foreach (var (factor, constant) in new[] { (product.Left, product.Right), (product.Right, product.Left) }) {
      if (!Positive(constant, out var multiplied))
        continue;

      if (Find(factor, context) is { } quotient)
        return IrShape.Same(Images.Unwiden(quotient.Value), Images.Unwiden(dividend)) ? this.Apply(expression, quotient, multiplied, context) : null;

      if (Resolve(factor, context) is not IrBinary { Op: IrBinaryOp.Divide, Right: IrConst } division || !Positive(division.Right, out var divisor)
        || division.Type != expression.Type || !IrShape.Same(division.Left, dividend) || context.Target.RemainderFromQuotient)
        continue;

      if (multiplied != divisor) {
        context.Note?.Invoke("O0126 not applied: remainder uses another divisor");
        return null;
      }

      if (!IrShape.IsTrapFree(expression)) {
        context.Note?.Invoke("O0126 not applied: operand may trap or have effects");
        return null;
      }

      var name = (division.Type.IsSigned ? "S" : "U") + "Rem";
      return context.Certify(this.Id, new IrBinary(IrBinaryOp.Remainder, division.Left, division.Right),
        string.Create(CultureInfo.InvariantCulture, $"{name}.{division.Type} by {divisor} from the quotient"), ProofClass);
    }

    return null;
  }

  /// <summary>The class of the proof: the certificate, a precondition checked in exact integers, of a rule validated exhaustively.</summary>
  private const string ProofClass = "validated-rule";

  /// <summary>An expression through the definitions of the stable locals it names, one after another.</summary>
  private static IrExpr Resolve(IrExpr expression, RuleContext context) {
    for (var step = 0; step < 16 && Images.Through(Images.Peel(expression), context) is var next && !ReferenceEquals(next, expression); ++step)
      expression = next;

    return expression;
  }

  /// <summary>An unsigned or a corrected signed reciprocal quotient.</summary>
  private static Quotient? Find(IrExpr expression, RuleContext context) {
    expression = Resolve(expression, context);
    return Unsigned(expression, context) ?? Corrected(expression, context);
  }

  /// <summary>The dividend the division takes: the value as the multiplication reads it, without widenings to a type of 32 bits or more.</summary>
  private static IrExpr Dividend(IrExpr value) => Images.Unwiden(value) is { Type.IsWideInteger: true } original ? original : value;

  /// <summary>Whether a shift is logical on its operand: written so, or of an unsigned type.</summary>
  private static bool Logical(IrBinary shift) => shift.Op == IrBinaryOp.ShiftRightLogical || shift.Op == IrBinaryOp.ShiftRight && !shift.Type.IsSigned;

  private static Quotient? Unsigned(IrExpr expression, RuleContext context) {
    if (expression is not IrBinary { Op: IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical, Right: IrConst } shift || !Images.IsConstant(shift.Right, out var s))
      return null;

    var inner = Resolve(shift.Left, context);
    if (High(inner, context, signed: false) is { } high && Logical(shift))
      return high with { Shift = high.Shift + (int)s };

    if (inner is IrBinary { Op: IrBinaryOp.Add } sum && Logical(shift)) {
      foreach (var (t, half) in new[] { (sum.Left, sum.Right), (sum.Right, sum.Left) })
        if (High(Resolve(t, context), context, signed: false) is { } added && added.Shift == t.Type.Bits
          && Images.Peel(half) is IrBinary { Op: IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical } halved && Logical(halved)
          && Images.IsConstant(halved.Right, out var one) && one == 1
          && Images.Peel(halved.Left) is IrBinary { Op: IrBinaryOp.Subtract } difference
          && IrShape.Same(Images.Unwiden(difference.Left), Images.Unwiden(added.Value)) && IrShape.Same(Resolve(difference.Right, context), Resolve(t, context)))
          return added with { Multiplier = (BigInteger.One << t.Type.Bits) + added.Multiplier, Shift = added.Shift + 1 + (int)s, Form = "add-back" };
    }

    // A narrow operand multiplied in a wider type and shifted there; the shift may be arithmetic only where the product stays positive.
    if (inner is IrBinary { Op: IrBinaryOp.Multiply } multiply && Factors(multiply) is var (value, multiplier) && value is not null
      && !Images.Unwiden(value).Type.IsSigned) {
      var limit = BigInteger.One << (multiply.Type.Bits - (Logical(shift) ? 0 : 1));
      var form = multiply.Type.Bits > Images.Unwiden(value).Type.Bits ? "widened-multiply" : "multiply";
      return new(value, multiplier, (int)s, false, form, multiplier * Maximum(value) >= limit);
    }

    return null;
  }

  /// <summary>The high product <c>MulHigh(x, M)</c> or <c>(T)((x * M) &gt;&gt; j)</c> of a signedness, with the shift it stands for.</summary>
  private static Quotient? High(IrExpr expression, RuleContext context, bool signed) {
    if (expression is IrIntrinsic { Op: IrIntrinsicOp.MulHigh, Arguments: [var left, var right] } high && high.Type.IsSigned == signed) {
      var (value, multiplier) = Positive(right, out var m) ? (left, m) : Positive(left, out var n) ? (right, n) : (null, BigInteger.Zero);
      return value is null ? null : new(value, multiplier, high.Type.Bits, signed, "mul-high", false);
    }

    if (expression is not IrConvert { Contract: IrConversion.Wrap, Operand: IrBinary { Op: IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical } shift } truncation
      || truncation.Type.Bits >= shift.Type.Bits || truncation.Type.IsSigned != signed || !Images.IsConstant(shift.Right, out var j)
      || Resolve(shift.Left, context) is not IrBinary { Op: IrBinaryOp.Multiply } product || Factors(product) is not ({ } operand, var factor)
      || Logical(shift) == signed || Images.Unwiden(operand).Type.IsSigned != signed || Images.Unwiden(operand).Type.Bits > truncation.Type.Bits)
      return null;

    // The full product must fit the multiplication's type, and the shifted product the truncated one.
    var bound = factor * BigInteger.Max(Maximum(operand), signed ? -Minimum(operand) : 0);
    var wraps = bound >= BigInteger.One << (product.Type.Bits - (signed ? 1 : 0)) || (bound >> (int)j) >= BigInteger.One << (truncation.Type.Bits - (signed ? 1 : 0));
    return new(operand, factor, (int)j, signed, "widened-multiply", wraps);
  }

  /// <summary>A multiplication of a value by a positive constant, in either order.</summary>
  private static (IrExpr? Value, BigInteger Multiplier) Factors(IrBinary product)
    => Positive(product.Right, out var right) ? (product.Left, right) : Positive(product.Left, out var left) ? (product.Right, left) : (null, BigInteger.Zero);

  /// <summary>Whether an expression is a positive integer constant, and its number in its type.</summary>
  private static bool Positive(IrExpr expression, out BigInteger value) {
    value = expression is IrConst { Type.IsInteger: true } constant ? (BigInteger)IrOperators.Number(constant.Type, constant.Value) : BigInteger.Zero;
    return value > 0;
  }

  /// <summary>An arithmetic high product, shifted or not: the signed quotient before its correction.</summary>
  private static Quotient? Signed(IrExpr expression, RuleContext context) {
    expression = Resolve(expression, context);
    if (High(expression, context, signed: true) is { } direct)
      return direct;

    if (expression is not IrBinary { Op: IrBinaryOp.ShiftRight, Type.IsSigned: true } shift || !Images.IsConstant(shift.Right, out var s))
      return null;

    var inner = Resolve(shift.Left, context);
    if (High(inner, context, signed: true) is { } high)
      return high with { Shift = high.Shift + (int)s };

    // A narrow operand multiplied in a wider signed type, where the full product of either sign fits.
    if (inner is IrBinary { Op: IrBinaryOp.Multiply } multiply && Factors(multiply) is ({ } value, var multiplier)
      && Images.Unwiden(value) is { Type.IsSigned: true } narrow && narrow.Type.Bits < multiply.Type.Bits)
      return new(value, multiplier, (int)s, true, "widened-multiply", multiplier << (narrow.Type.Bits - 1) >= BigInteger.One << (multiply.Type.Bits - 1));

    return null;
  }

  /// <summary><c>q - (x &gt;&gt; (w - 1))</c> or <c>q + (x &gt;&gt;&gt; (w - 1))</c>: the signed quotient corrected to truncate.</summary>
  private static Quotient? Corrected(IrExpr expression, RuleContext context) {
    if (expression is not IrBinary { Op: IrBinaryOp.Subtract or IrBinaryOp.Add } sum)
      return null;

    var pairs = sum.Op == IrBinaryOp.Subtract ? new[] { (sum.Left, sum.Right) } : new[] { (sum.Left, sum.Right), (sum.Right, sum.Left) };
    foreach (var (quotient, correction) in pairs) {
      if (Signed(quotient, context) is not { } found)
        continue;

      var x = Images.Unwiden(found.Value);
      var sign = Resolve(correction, context);
      // The sign bit as 0 or -1 is subtracted; as 0 or 1 it is added.
      var corrected = sign is IrBinary { Op: IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical } bit && Images.IsConstant(bit.Right, out var count)
        && (count == bit.Type.Bits - 1 || count == x.Type.Bits - 1 && !Logical(bit)) && IrShape.Same(Images.Unwiden(Images.Peel(bit.Left)), x)
        && (Logical(bit) ? sum.Op == IrBinaryOp.Add : sum.Op == IrBinaryOp.Subtract);
      if (corrected)
        return found;
    }

    return null;
  }

  private static BigInteger Maximum(IrExpr value) {
    var type = Images.Unwiden(value).Type;
    return type.IsSigned ? (BigInteger.One << (type.Bits - 1)) - 1 : (BigInteger.One << type.Bits) - 1;
  }

  private static BigInteger Minimum(IrExpr value) {
    var type = Images.Unwiden(value).Type;
    return type.IsSigned ? -(BigInteger.One << (type.Bits - 1)) : 0;
  }

  /// <summary>Derives the divisor from the multiplier and the shift and checks, in exact integers, that the quotient is exact.</summary>
  private static Certificate Certify(Quotient quotient) {
    var power = BigInteger.One << quotient.Shift;
    var multiplier = quotient.Multiplier;
    // The nearest divisor, so that a multiplier one below the reciprocal is still read as meant for it.
    var divisor = (2 * power + multiplier) / (2 * multiplier);
    var excess = multiplier * divisor - power;
    // Only a multiplier near a reciprocal, beyond a bare high product - the add-back's t - is worth a report.
    var meant = divisor >= 2 && BigInteger.Abs(excess) <= divisor && quotient.Shift != Images.Unwiden(quotient.Value).Type.Bits;
    return new(divisor, excess, Refusal(quotient, power, divisor, excess), meant);
  }

  /// <summary>Why the certificate fails, or <see langword="null"/> when the quotient is exact on the operand's range.</summary>
  private static string? Refusal(Quotient quotient, BigInteger power, BigInteger divisor, BigInteger excess) {
    if (quotient.Wraps)
      return "multiplication may wrap";

    var width = Images.Unwiden(quotient.Value).Type.Bits;
    if (quotient.Signed) {
      if (divisor < 2 || divisor > BigInteger.One << (width - 1) || quotient.Multiplier >= BigInteger.One << (width - 1))
        return "divisor out of range";

      return excess > 0 && excess << (width - 1) <= power ? null : NotExact;
    }

    var maximum = Maximum(quotient.Value);
    if (divisor < 2 || divisor > (maximum + 1) / 2)
      return "divisor out of range";

    var last = (maximum + 1) / divisor * divisor - 1;
    return excess >= 0 && excess * last < power && excess * maximum < power * (divisor - maximum % divisor) ? null : NotExact;
  }

}
