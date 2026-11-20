using System.Numerics;
using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize.Rules;

/// <summary>
/// O0125: a sum or difference of shifted copies of one value - <c>(x &lt;&lt; 3) + (x &lt;&lt; 1)</c>,
/// <c>(x &lt;&lt; 4) - x</c>, <c>((x &lt;&lt; 2) + x) &lt;&lt; 1</c>, chains of them - becomes a
/// multiplication by the constant they add up to.
/// <para>
/// The expression is read as a linear form <c>c · x</c> modulo <c>2^w</c>, the width of its type:
/// wrapping addition and subtraction add coefficients, negation negates, a shift to the left by a
/// constant (which the tree IR takes modulo the width) and a multiplication by a constant scale it,
/// conversions between the signed and unsigned type of one width change no bit, and stable locals
/// stand for their definitions. Every other node is a leaf and must be the one value <c>x</c>, which
/// must occur at least twice. A coefficient of 0, 1 or a power of two is left to constant folding and
/// to the shift form; negative coefficients, minus one among them, are rewritten.
/// </para>
/// <para>
/// Every replacement requires a host proof of the actual expression. A chain that would be a
/// multiplication but for a checked operation, an <c>|</c> or <c>^</c> in place of <c>+</c>, a second
/// value or a shift by a variable amount is refused with its reason. The value is evaluated once
/// instead of several times and must have no effect and not trap.
/// </para>
/// </summary>
public sealed class O0125ShiftAddMultiply : ExpressionRule {

  private const int MaximumDepth = 64;

  /// <summary>Creates the rule.</summary>
  public O0125ShiftAddMultiply() : base("O0125", "multiplication by a constant from shifts and adds") { }

  /// <inheritdoc/>
  protected override IrExpr? Recognize(IrExpr expression, RuleContext context) {
    if (expression is not IrBinary {
      Op: IrBinaryOp.Add or IrBinaryOp.Subtract or IrBinaryOp.AddChecked or IrBinaryOp.SubtractChecked
      or IrBinaryOp.ShiftLeft or IrBinaryOp.Multiply or IrBinaryOp.MultiplyChecked or IrBinaryOp.Or or IrBinaryOp.Xor
    } and not IrUnary { Op: IrUnaryOp.Negate or IrUnaryOp.NegateChecked }
      || !expression.Type.IsInteger)
      return null;

    var form = new LinearForm(expression.Type.Bits, context);
    if (form.Read(expression, 0) is not { } coefficient || form.Atom is not { } atom || form.Occurrences < 2)
      return null;

    // Two values with fewer than two scaled terms are ordinary arithmetic, not a chain that was meant as one product.
    if (form.Mixed && form.Scaled < 2)
      return null;

    var reason = form.Mixed ? "more than one variable" : form.Refusal;
    if (reason is null && (coefficient == 0 || BitOperations.IsPow2(coefficient)))
      reason = "coefficient is trivial";
    if (reason is not null) {
      context.Note?.Invoke($"O0125 not applied: {reason}");
      return null;
    }

    if (!IrShape.IsTrapFree(expression)) {
      context.Note?.Invoke("O0125 not applied: operand may trap or have effects");
      return null;
    }

    var factor = Images.Constant(expression.Type, unchecked((long)coefficient));
    var after = new IrBinary(IrBinaryOp.Multiply, Images.Fit(atom, expression.Type), factor);
    return context.Confirm(this.Id, expression, after, $"Mul.{expression.Type} by {IrOperators.Number(expression.Type, factor.Value)} from {form.Occurrences} shift/add terms");
  }

  /// <summary>
  /// Reads an expression as <c>c · x</c> modulo <c>2^w</c>. A refusal is recorded and reading goes on as
  /// if the operation were the wrapping one, so that only a chain that is otherwise a multiplication reports it.
  /// </summary>
  private sealed class LinearForm(int width, RuleContext context) {

    private readonly ulong _mask = width == 64 ? ulong.MaxValue : (1UL << width) - 1;

    /// <summary>The one value the form multiplies.</summary>
    public IrExpr? Atom { get; private set; }

    /// <summary>How often the value occurs.</summary>
    public int Occurrences { get; private set; }

    /// <summary>How many shifts and multiplications by a constant scale it.</summary>
    public int Scaled { get; private set; }

    /// <summary>Whether a second value occurs.</summary>
    public bool Mixed { get; private set; }

    /// <summary>Why the chain is refused although it has the shape of a multiplication.</summary>
    public string? Refusal { get; private set; }

    private void Refuse(string reason) => this.Refusal ??= reason;

    /// <summary>The coefficient of the value in an expression, or <see langword="null"/> when it is not linear in one value.</summary>
    public ulong? Read(IrExpr expression, int depth) {
      if (depth > MaximumDepth)
        return null;

      switch (expression) {
        // Zero adds nothing - 0 - x is how an unsigned negation is written; any other constant is an offset, not a product.
        case IrConst constant:
          return constant.Value == 0 ? 0 : null;
        case IrLocalRef reference when context.Definition(reference.Local) is { } definition:
          return this.Read(definition, depth + 1);
        case IrConvert { Contract: IrConversion.Wrap, Operand.Type.IsInteger: true, Type.IsInteger: true } conversion
          when conversion.Operand.Type.Bits == conversion.Type.Bits:
          return this.Read(conversion.Operand, depth + 1);
        case IrUnary { Op: IrUnaryOp.Negate or IrUnaryOp.NegateChecked } negation:
          if (negation.Op == IrUnaryOp.NegateChecked)
            this.Refuse("non-wrapping arithmetic");
          return this.Read(negation.Operand, depth + 1) is { } negated ? (0 - negated) & this._mask : null;
        // An | or ^ is read as a combiner only when it combines scaled terms; otherwise it is a value of its own, such as a ^ b.
        case IrBinary { Op: IrBinaryOp.Or or IrBinaryOp.Xor } combined when !IsScaled(combined.Left) && !IsScaled(combined.Right):
          return this.Leaf(combined);
        case IrBinary { Op: IrBinaryOp.Add or IrBinaryOp.Subtract or IrBinaryOp.AddChecked or IrBinaryOp.SubtractChecked or IrBinaryOp.Or or IrBinaryOp.Xor } sum:
          if (this.Read(sum.Left, depth + 1) is not { } left || this.Read(sum.Right, depth + 1) is not { } right)
            return null;
          if (sum.Op is IrBinaryOp.AddChecked or IrBinaryOp.SubtractChecked)
            this.Refuse("non-wrapping arithmetic");
          if (sum.Op is IrBinaryOp.Or or IrBinaryOp.Xor)
            this.Refuse("combiner is not Add or Sub");
          return (sum.Op is IrBinaryOp.Subtract or IrBinaryOp.SubtractChecked ? left - right : left + right) & this._mask;
        case IrBinary { Op: IrBinaryOp.ShiftLeft } shift:
          if (this.Read(shift.Left, depth + 1) is not { } shifted)
            return null;
          ++this.Scaled;
          if (!Images.IsConstant(shift.Right, out var count)) {
            this.Refuse("non-constant shift amount");
            return shifted;
          }

          return (shifted << (int)(count & (width - 1))) & this._mask;
        case IrBinary { Op: IrBinaryOp.Multiply or IrBinaryOp.MultiplyChecked } product:
          var (factor, operand) = Images.IsConstant(product.Right, out var byRight) ? (byRight, product.Left)
            : Images.IsConstant(product.Left, out var byLeft) ? (byLeft, product.Right) : (0L, null);
          if (operand is null)
            return this.Leaf(expression);
          if (this.Read(operand, depth + 1) is not { } multiplied)
            return null;
          ++this.Scaled;
          if (product.Op == IrBinaryOp.MultiplyChecked)
            this.Refuse("non-wrapping arithmetic");
          return unchecked(multiplied * (ulong)factor) & this._mask;
        // Any other operator - a right shift, a mask - makes the expression nonlinear in its operands.
        case IrBinary or IrUnary:
          return null;
        default:
          return this.Leaf(expression);
      }
    }

    /// <summary>Whether an operand is scaled: a shift to the left or a multiplication by a constant.</summary>
    private static bool IsScaled(IrExpr operand) => Images.Peel(operand) is IrBinary { Op: IrBinaryOp.ShiftLeft }
      or IrBinary { Op: IrBinaryOp.Multiply or IrBinaryOp.MultiplyChecked, Right: IrConst } or IrBinary { Op: IrBinaryOp.Multiply or IrBinaryOp.MultiplyChecked, Left: IrConst };

    /// <summary>A leaf has the coefficient one when it is the value; a second value is recorded as a refusal.</summary>
    private ulong? Leaf(IrExpr expression) {
      if (!expression.Type.IsInteger || expression.Type.Bits != width)
        return null;

      ++this.Occurrences;
      if (this.Atom is null)
        this.Atom = expression;
      else if (!IrShape.Same(this.Atom, expression))
        this.Mixed = true;
      return 1;
    }
  }

}
