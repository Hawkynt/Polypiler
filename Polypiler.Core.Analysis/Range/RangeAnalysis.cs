using System;
using System.Linq;
using Polypiler.Core.Analysis.Dataflow;
using Polypiler.Core.IR;

namespace Polypiler.Core.Analysis.Range;

/// <summary>
/// The intervals of the integer and truth variables of a function: the minimal range provider the
/// pointer provenance (#93) stands on, until the full range analysis (#94) replaces it. Constants are
/// exact; addition, subtraction and multiplication are exact while they stay within the type and the
/// whole type where they may wrap; a checked operation keeps what does not trap; a mask with a
/// non-negative operand, a remainder and a division by a positive divisor bound the result. The edges
/// of the comparisons <c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c>, <c>&gt;=</c> and <c>==</c> with a variable
/// on either side narrow that variable; a comparison the ranges decide is decided.
/// </summary>
public sealed class RangeAnalysis : TreeDataflow<ValueRange>, IFactProvider<ValueRange>, IComparisonDecider {

  /// <summary>Prepares the analysis of a function; it runs on the first query.</summary>
  /// <param name="function">The function.</param>
  /// <param name="budget">The most statements the walk may visit.</param>
  public RangeAnalysis(IrFunction function, long budget = Constant.ConstantAnalysis.DefaultBudget) : base(function, budget) { }

  /// <inheritdoc/>
  public Truth Decide(IrExpr comparison, ProgramPoint point) {
    this.Layout.Check(comparison, point);
    return this.StateAt(point) is { } state ? this.Test(comparison, state) : Truth.Unknown;
  }

  /// <summary>
  /// The range of an expression a statement at a point evaluates, without the check that its variables
  /// are defined there: one bound by the statement itself, in its condition, is too.
  /// </summary>
  internal ValueRange RangeAt(IrExpr value, ProgramPoint point) => this.StateAt(point) is { } state ? this.Evaluate(value, state) : ValueRange.Unreachable;

  /// <inheritdoc/>
  protected override ValueRange Evaluate(IrExpr expression, FactState<ValueRange> state) {
    switch (expression) {
      case IrConst constant when constant.Type.IsInteger:
        return ValueRange.Of(IrOperators.Number(constant.Type, constant.Value));
      case IrConst constant when constant.Type == IrType.Bool:
        return ValueRange.Of(constant.Value);
      case IrLocalRef reference:
        return RangeDomain.Instance.Meet(state.Fact(reference.Local), ValueRange.Of(reference.Type));
      case IrBinary { Op: >= IrBinaryOp.Equal } or IrUnary { Op: IrUnaryOp.LogicalNot }:
        return this.Test(expression, state) switch {
          Truth.True => ValueRange.Of(1),
          Truth.False => ValueRange.Of(0),
          _ => new ValueRange(0, 1)
        };
      case IrBinary binary when binary.Type.IsInteger && binary.Left.Type == binary.Type:
        return this.Arithmetic(binary, this.Evaluate(binary.Left, state), this.Evaluate(binary.Right, state));
      case IrConvert conversion when conversion.Type.IsInteger:
        var operand = this.Evaluate(conversion.Operand, state);
        return ValueRange.Of(conversion.Type).Covers(operand) && conversion.Operand.Type.IsInteger ? operand : ValueRange.Of(conversion.Type);
      case IrConditional choice:
        return RangeDomain.Instance.Join(this.Evaluate(choice.WhenTrue, state), this.Evaluate(choice.WhenFalse, state));
      default:
        return ValueRange.Of(expression.Type);
    }
  }

  /// <inheritdoc/>
  protected override Truth Test(IrExpr condition, FactState<ValueRange> state) {
    switch (condition) {
      case IrUnary { Op: IrUnaryOp.LogicalNot } negation:
        return this.Test(negation.Operand, state) switch { Truth.True => Truth.False, Truth.False => Truth.True, _ => Truth.Unknown };
      case IrBinary { Op: IrBinaryOp.LogicalAnd or IrBinaryOp.LogicalOr } logical:
        var (left, right) = (this.Test(logical.Left, state), this.Test(logical.Right, state));
        var absorbing = logical.Op == IrBinaryOp.LogicalAnd ? Truth.False : Truth.True;
        return left == absorbing || right == absorbing ? absorbing
          : left != Truth.Unknown && right != Truth.Unknown ? (absorbing == Truth.False ? Truth.True : Truth.False)
          : Truth.Unknown;
      case IrBinary { Op: >= IrBinaryOp.Equal and <= IrBinaryOp.GreaterOrEqual } comparison when Ordered(comparison.Left.Type):
        return Compare(comparison.Op, this.Evaluate(comparison.Left, state), this.Evaluate(comparison.Right, state));
      case IrBinary { Op: >= IrBinaryOp.Equal and <= IrBinaryOp.GreaterOrEqual }:
        // Floating-point numbers are no ranges of this domain.
        return Truth.Unknown;
      default:
        var range = this.Evaluate(condition, state);
        return condition.Type != IrType.Bool || range.Min != range.Max ? Truth.Unknown : range.Min == 1 ? Truth.True : Truth.False;
    }
  }

  /// <inheritdoc/>
  protected override FactState<ValueRange> Refine(IrExpr condition, bool outcome, FactState<ValueRange> state, IrStmt branch) {
    switch (condition) {
      case IrLocalRef reference when reference.Type == IrType.Bool:
        return state.With(reference.Local, ValueRange.Of(outcome ? 1 : 0), [branch]);
      case IrUnary { Op: IrUnaryOp.LogicalNot } negation:
        return this.Refine(negation.Operand, !outcome, state, branch);
      case IrBinary { Op: IrBinaryOp.LogicalAnd } conjunction when outcome:
        return this.Refine(conjunction.Right, true, this.Refine(conjunction.Left, true, state, branch), branch);
      case IrBinary { Op: IrBinaryOp.LogicalOr } disjunction when !outcome:
        return this.Refine(disjunction.Right, false, this.Refine(disjunction.Left, false, state, branch), branch);
      case IrBinary { Op: >= IrBinaryOp.Equal and <= IrBinaryOp.GreaterOrEqual } comparison when Ordered(comparison.Left.Type):
        var op = outcome ? comparison.Op : Negated(comparison.Op);
        state = this.Bound(comparison.Left, op, comparison.Right, state, branch);
        return this.Bound(comparison.Right, Mirrored(op), comparison.Left, state, branch);
      default:
        return state;
    }
  }

  /// <summary>The state where <c>variable op value</c> holds: the variable's range narrowed by the value's.</summary>
  private FactState<ValueRange> Bound(IrExpr variable, IrBinaryOp op, IrExpr value, FactState<ValueRange> state, IrStmt branch) {
    if (variable is not IrLocalRef reference)
      return state;

    var bound = this.Evaluate(value, state);
    var allowed = op switch {
      IrBinaryOp.Less => new ValueRange(Int128.MinValue, bound.Max - 1),
      IrBinaryOp.LessOrEqual => new ValueRange(Int128.MinValue, bound.Max),
      IrBinaryOp.Greater => new ValueRange(bound.Min + 1, Int128.MaxValue),
      IrBinaryOp.GreaterOrEqual => new ValueRange(bound.Min, Int128.MaxValue),
      IrBinaryOp.Equal => bound,
      _ => ValueRange.Unknown
    };
    if (bound.IsEmpty || allowed.IsUnknown)
      return state;

    var because = state.Because(reference.Local).Concat(IrTree.Nodes(value).OfType<IrLocalRef>().SelectMany(other => state.Because(other.Local))).Append(branch);
    return state.With(reference.Local, RangeDomain.Instance.Meet(this.Evaluate(variable, state), allowed), because);
  }

  private ValueRange Arithmetic(IrBinary binary, ValueRange left, ValueRange right) {
    var type = ValueRange.Of(binary.Type);
    if (left.IsEmpty || right.IsEmpty)
      return ValueRange.Unreachable;

    switch (binary.Op) {
      case IrBinaryOp.Add or IrBinaryOp.Subtract or IrBinaryOp.Multiply or IrBinaryOp.AddChecked or IrBinaryOp.SubtractChecked or IrBinaryOp.MultiplyChecked:
        var exact = Exact(binary.Op, left, right);
        if (IrOperators.Traps(binary.Op))
          return exact is { } kept ? RangeDomain.Instance.Meet(kept, type) : type;

        return exact is { } result && type.Covers(result) ? result : type;
      case IrBinaryOp.And when left.Min >= 0 || right.Min >= 0:
        return new ValueRange(0, left.Min >= 0 && right.Min >= 0 ? Int128.Min(left.Max, right.Max) : left.Min >= 0 ? left.Max : right.Max);
      case IrBinaryOp.Remainder when left.Min >= 0 && right.Min > 0:
        return new ValueRange(0, Int128.Min(left.Max, right.Max - 1));
      case IrBinaryOp.Divide when left.Min >= 0 && right.Min > 0:
        return new ValueRange(left.Min / right.Max, left.Max / right.Min);
      default:
        return type;
    }
  }

  /// <summary>The exact range of a sum, difference or product; <see langword="null"/> when a bound leaves <see cref="Int128"/>.</summary>
  private static ValueRange? Exact(IrBinaryOp op, ValueRange left, ValueRange right) {
    try {
      checked {
        switch (op) {
          case IrBinaryOp.Add or IrBinaryOp.AddChecked:
            return new ValueRange(left.Min + right.Min, left.Max + right.Max);
          case IrBinaryOp.Subtract or IrBinaryOp.SubtractChecked:
            return new ValueRange(left.Min - right.Max, left.Max - right.Min);
          default:
            Int128[] corners = [left.Min * right.Min, left.Min * right.Max, left.Max * right.Min, left.Max * right.Max];
            return new ValueRange(corners.Min(), corners.Max());
        }
      }
    } catch (OverflowException) {
      return null;
    }
  }

  private static Truth Compare(IrBinaryOp op, ValueRange left, ValueRange right) {
    if (left.IsEmpty || right.IsEmpty)
      return Truth.Unknown;

    bool? holds = op switch {
      IrBinaryOp.Less => left.Max < right.Min ? true : left.Min >= right.Max ? false : null,
      IrBinaryOp.LessOrEqual => left.Max <= right.Min ? true : left.Min > right.Max ? false : null,
      IrBinaryOp.Greater => left.Min > right.Max ? true : left.Max <= right.Min ? false : null,
      IrBinaryOp.GreaterOrEqual => left.Min >= right.Max ? true : left.Max < right.Min ? false : null,
      _ => left.Min == left.Max && right.Min == right.Max && left.Min == right.Min ? true : left.Max < right.Min || right.Max < left.Min ? false : null
    };
    var truth = holds switch { true => Truth.True, false => Truth.False, _ => Truth.Unknown };
    return op != IrBinaryOp.NotEqual ? truth : truth switch { Truth.True => Truth.False, Truth.False => Truth.True, _ => Truth.Unknown };
  }

  private static bool Ordered(IrType type) => type.IsInteger || type == IrType.Bool;

  private static IrBinaryOp Negated(IrBinaryOp op) => op switch {
    IrBinaryOp.Less => IrBinaryOp.GreaterOrEqual,
    IrBinaryOp.LessOrEqual => IrBinaryOp.Greater,
    IrBinaryOp.Greater => IrBinaryOp.LessOrEqual,
    IrBinaryOp.GreaterOrEqual => IrBinaryOp.Less,
    IrBinaryOp.Equal => IrBinaryOp.NotEqual,
    _ => IrBinaryOp.Equal
  };

  private static IrBinaryOp Mirrored(IrBinaryOp op) => op switch {
    IrBinaryOp.Less => IrBinaryOp.Greater,
    IrBinaryOp.LessOrEqual => IrBinaryOp.GreaterOrEqual,
    IrBinaryOp.Greater => IrBinaryOp.Less,
    IrBinaryOp.GreaterOrEqual => IrBinaryOp.LessOrEqual,
    _ => op
  };

}
