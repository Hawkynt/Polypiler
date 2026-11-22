using System.Globalization;
using System.Numerics;
using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize.Rules;

/// <summary>
/// O0104: the de Bruijn bit scans become zero counts - <c>table[((x &amp; -x) * C) &gt;&gt; (w - log2 w)]</c>
/// becomes <see cref="IrIntrinsicOp.TrailingZeroCount"/> of <c>x</c>, and the same lookup of the smear
/// <c>x | x &gt;&gt; 1 | … | x &gt;&gt; w/2</c> becomes the floor of the base-2 logarithm,
/// <c>(w - 1) - </c><see cref="IrIntrinsicOp.LeadingZeroCount"/>, at 32 and 64 bits.
/// <para>
/// The table is a constant of the module, which the frontends make of an array nothing writes; one
/// that may be written is a global and is refused. The table check is complete: for a non-zero
/// <c>x</c> the isolated bit takes only the <c>w</c> powers of two and the smear only the <c>w</c>
/// values <c>2^(i+1) - 1</c>, so the table must hold <c>i</c> at each of their indices; the first index
/// it fails at is reported. For <c>x = 0</c> the lookup reads the table's first entry, which the
/// replacement keeps by a selection on <c>x == 0</c>. The smear is commonly a run of assignments, which
/// the rule reads as one expression. Every replacement requires a host proof of the actual lookup,
/// table included.
/// </para>
/// </summary>
public sealed class O0104ZeroCountDeBruijn : ExpressionRule {

  /// <summary>Creates the rule.</summary>
  public O0104ZeroCountDeBruijn() : base("O0104", "zero count from a de Bruijn multiply and table lookup", runs: true) { }

  /// <inheritdoc/>
  protected override IrExpr? Recognize(IrExpr expression, RuleContext context) {
    // The lookup is often converted to the type of the variable it is stored in; the conversion stays.
    var (load, conversion) = expression switch {
      IrArrayLoad direct => (direct, (IrConvert?)null),
      IrConvert { Contract: IrConversion.Wrap, Operand: IrArrayLoad inner } converted => (inner, converted),
      _ => (null, null)
    };
    if (load is null || !load.Type.IsInteger)
      return null;

    var (candidate, intrinsic, reason) = Lookup(load);
    if (candidate is null) {
      if (reason is not null)
        context.Note?.Invoke($"O0104 not applied: {reason}");
      return null;
    }

    if (!IrShape.IsTrapFree(load)) {
      context.Note?.Invoke("O0104 not applied: operand may trap or have effects");
      return null;
    }

    var description = $"{intrinsic} from de Bruijn lookup";
    return context.Confirm(this.Id, load, candidate, description) is { } replaced
      ? conversion is null ? replaced : conversion with { Operand = replaced }
      : null;
  }

  /// <summary>An expression without any integer conversion around it.</summary>
  private static IrExpr Strip(IrExpr expression) {
    while (expression is IrConvert { Contract: IrConversion.Wrap, Operand.Type.IsInteger: true, Type.IsInteger: true } conversion)
      expression = conversion.Operand;

    return expression;
  }

  /// <summary>The replacement of a lookup, the operation it recovers, or why it is refused.</summary>
  private static (IrExpr? Candidate, string Intrinsic, string? Reason) Lookup(IrArrayLoad load) {
    // Only a table read at a multiply-shift index is a candidate; any other table lookup is no concern of the rule.
    if (Strip(load.Index) is not IrBinary { Op: IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical, Right: IrConst shift } index
      || Strip(index.Left) is not IrBinary { Op: IrBinaryOp.Multiply } product
      || (Images.Peel(product.Right), Images.Peel(product.Left)) switch {
        (IrConst, _) or (_, IrConst) => false,
        _ => true
      })
      return (null, "", null);

    var (multiplier, operand) = Images.Peel(product.Right) is IrConst right ? (right, product.Left) : ((IrConst)Images.Peel(product.Left), product.Right);
    var form = Form(operand, out var value);
    if (form is null)
      return (null, "", null);
    if (value is null)
      return (null, "", "operand is not an isolated bit");

    // The scan's width is that of the product as it is shifted: a narrower conversion on the way cuts it.
    var width = product.Type.Bits;
    for (var between = index.Left; between is IrConvert conversion; between = conversion.Operand)
      width = System.Math.Min(width, conversion.Type.Bits);

    if (width is not (32 or 64) || width != product.Type.Bits || value.Type.Bits != width)
      return (null, "", "unsupported width");

    var indexBits = BitOperations.Log2((uint)width);
    if (shift.Value != width - indexBits || index.Op == IrBinaryOp.ShiftRight && index.Type.IsSigned)
      return (null, "", "index is not multiply-shift");

    if (load.Array is not IrConstRef { Constant: var table })
      return (null, "", "table not constant");
    if (table.Values.Count != width)
      return (null, "", $"table check failed: {table.Values.Count.ToString(CultureInfo.InvariantCulture)} entries for {width.ToString(CultureInfo.InvariantCulture)} positions");

    var element = load.Type;
    var mask = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
    for (var position = 0; position < width; ++position) {
      // The values the operand takes for a non-zero input: the isolated bit, or the smear below the highest bit.
      var reached = form == "trailing" ? 1UL << position : (position == 63 ? ulong.MaxValue : (1UL << (position + 1)) - 1);
      var at = (int)((unchecked(reached * (ulong)multiplier.Value) & mask) >> (width - indexBits));
      if (IrOperators.Number(element, table.Values[at]) != position)
        return (null, "", $"table check failed at index {at.ToString(CultureInfo.InvariantCulture)}");
    }

    IrExpr count = form == "trailing"
      ? Images.Call(IrIntrinsicOp.TrailingZeroCount, IrType.I32, value)
      : new IrBinary(IrBinaryOp.Subtract, Images.Constant(IrType.I32, width - 1), Images.Call(IrIntrinsicOp.LeadingZeroCount, IrType.I32, value));
    var zero = new IrBinary(IrBinaryOp.Equal, value, Images.Constant(value.Type, 0));
    var first = Images.Constant(element, table.Values[0]);
    var candidate = new IrConditional(zero, first, Images.Fit(count, element));
    return (candidate, form == "trailing" ? $"TrailingZeroCount.{value.Type}" : $"LeadingZeroCount.{value.Type}", null);
  }

  /// <summary>
  /// The form of the multiplied operand and its value <c>x</c>: <c>"trailing"</c> for an and with a
  /// negation, <c>"log"</c> for an or-smear; <c>"trailing"</c> with a <see langword="null"/> value for an
  /// and that isolates no bit; <see langword="null"/> for any other operand.
  /// </summary>
  private static string? Form(IrExpr operand, out IrExpr? value) {
    value = null;
    switch (Images.Peel(operand)) {
      case IrBinary { Op: IrBinaryOp.And } and:
        foreach (var (kept, negated) in new[] { (and.Left, and.Right), (and.Right, and.Left) })
          if (Images.Negated(Strip(negated)) is { } inner && IrShape.Same(Strip(inner), Strip(kept))) {
            value = kept;
            break;
          }

        return "trailing";
      case IrBinary { Op: IrBinaryOp.Or } or:
        // The smear ors a value with its right shifts; its innermost value is x. The proof checks the shifts.
        IrExpr current = or;
        while (Smeared(current) is { } previous)
          current = previous;

        // An or that is no smear is some other hash of x; only an and was meant to isolate a bit.
        if (ReferenceEquals(current, or))
          return null;

        value = current;
        return "log";
      default:
        return null;
    }
  }

  /// <summary>The value one smear step <c>v | v &gt;&gt; s</c>, in either order, ors with its shift; <see langword="null"/> otherwise.</summary>
  private static IrExpr? Smeared(IrExpr step) {
    if (Images.Peel(step) is not IrBinary { Op: IrBinaryOp.Or } or)
      return null;

    foreach (var (kept, shifted) in new[] { (or.Left, or.Right), (or.Right, or.Left) })
      if (Images.Peel(shifted) is IrBinary { Op: IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical, Right: IrConst } shift
        && IrShape.Same(Images.Peel(shift.Left), Images.Peel(kept)))
        return kept;

    return null;
  }

}
