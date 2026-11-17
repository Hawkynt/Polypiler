using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.Analysis.Dataflow;
using Polypiler.Core.IR;

namespace Polypiler.Core.Analysis.Constant;

/// <summary>
/// The constant domain's provider, the first analysis behind the facade (#88): which integer and truth
/// variables hold one value at a point. An operation on constants is folded by the interpreter itself, so
/// a folded value is the value the operation has; one that traps folds to nothing. The edge of a branch
/// learns from its condition: <c>x == 7</c> makes <c>x</c> the constant 7 where it holds, a truth variable
/// is its outcome, and <c>!</c>, <c>&amp;&amp;</c> and <c>||</c> pass that on.
/// </summary>
public sealed class ConstantAnalysis : TreeDataflow<ConstantFact>, IFactProvider<ConstantFact>, IComparisonDecider {

  /// <summary>The budget of statements visited unless the caller sets one.</summary>
  public const long DefaultBudget = 1_000_000;

  /// <summary>Prepares the analysis of a function; it runs on the first query.</summary>
  /// <param name="function">The function.</param>
  /// <param name="budget">The most statements the walk may visit.</param>
  public ConstantAnalysis(IrFunction function, long budget = DefaultBudget) : base(function, budget) { }

  /// <inheritdoc/>
  public Truth Decide(IrExpr comparison, ProgramPoint point) {
    this.Layout.Check(comparison, point);
    return this.StateAt(point) is { } state ? this.Test(comparison, state) : Truth.Unknown;
  }

  /// <inheritdoc/>
  protected override ConstantFact Evaluate(IrExpr expression, FactState<ConstantFact> state) {
    switch (expression) {
      case IrConst constant:
        return Scalar(constant.Type) ? ConstantFact.Of(constant.Value) : ConstantFact.Unknown;
      case IrLocalRef reference:
        return state.Fact(reference.Local);
      case IrBinary { Op: IrBinaryOp.LogicalAnd or IrBinaryOp.LogicalOr } logical:
        var left = this.Evaluate(logical.Left, state);
        if (!left.IsConstant)
          return ConstantFact.Unknown;

        // false && x and true || x never evaluate x.
        return (left.Value != 0) == (logical.Op == IrBinaryOp.LogicalOr) ? left : this.Evaluate(logical.Right, state);
      case IrConditional choice:
        var condition = this.Evaluate(choice.Condition, state);
        return condition.IsConstant ? this.Evaluate(condition.Value != 0 ? choice.WhenTrue : choice.WhenFalse, state) : ConstantFact.Unknown;
      case IrUnary or IrBinary or IrConvert when Scalar(expression.Type):
        var operands = IrTree.Operands(expression);
        var facts = operands.Select(operand => this.Evaluate(operand, state)).ToList();
        if (facts.Any(fact => !fact.IsConstant) || !operands.All(operand => Scalar(operand.Type)))
          return ConstantFact.Unknown;

        return Fold(IrTree.With(expression, [.. operands.Select((operand, index) => new IrConst(operand.Type, facts[index].Value))]));
      default:
        return ConstantFact.Unknown;
    }
  }

  /// <inheritdoc/>
  protected override Truth Test(IrExpr condition, FactState<ConstantFact> state) {
    var fact = this.Evaluate(condition, state);
    return !fact.IsConstant ? Truth.Unknown : fact.Value != 0 ? Truth.True : Truth.False;
  }

  /// <inheritdoc/>
  protected override FactState<ConstantFact> Refine(IrExpr condition, bool outcome, FactState<ConstantFact> state, IrStmt branch) {
    switch (condition) {
      case IrLocalRef reference when reference.Type == IrType.Bool:
        return state.With(reference.Local, ConstantFact.Of(outcome ? 1 : 0), [branch]);
      case IrUnary { Op: IrUnaryOp.LogicalNot } negation:
        return this.Refine(negation.Operand, !outcome, state, branch);
      case IrBinary { Op: IrBinaryOp.LogicalAnd } conjunction when outcome:
        return this.Refine(conjunction.Right, true, this.Refine(conjunction.Left, true, state, branch), branch);
      case IrBinary { Op: IrBinaryOp.LogicalOr } disjunction when !outcome:
        return this.Refine(disjunction.Right, false, this.Refine(disjunction.Left, false, state, branch), branch);
      case IrBinary { Op: IrBinaryOp.Equal or IrBinaryOp.NotEqual } equality when outcome == (equality.Op == IrBinaryOp.Equal):
        return this.Equal(equality.Left, equality.Right, this.Equal(equality.Right, equality.Left, state, branch), branch);
      default:
        return state;
    }
  }

  /// <summary>The state where a variable equals a value that is constant.</summary>
  private FactState<ConstantFact> Equal(IrExpr variable, IrExpr value, FactState<ConstantFact> state, IrStmt branch) {
    if (variable is not IrLocalRef reference || !Scalar(reference.Type) || this.Evaluate(value, state) is not { IsConstant: true } fact)
      return state;

    var because = IrTree.Nodes(value).OfType<IrLocalRef>().SelectMany(other => state.Because(other.Local)).Append(branch);
    return state.With(reference.Local, fact, because);
  }

  /// <summary>Whether values of a type are what the domain speaks of: integers and truth values.</summary>
  private static bool Scalar(IrType type) => type.IsInteger || type == IrType.Bool;

  /// <summary>An operation on constants as the interpreter computes it; Unknown when it traps.</summary>
  private static ConstantFact Fold(IrExpr operation) {
    var function = new IrFunction("value", true, operation.Type, [], [], new IrBlock([new IrReturn(operation)]));
    try {
      return ConstantFact.Of(IrInterpreter.Call(new IrModule("constant", [function]), function.Name, []) switch {
        bool truth => truth ? 1 : 0,
        ulong word => unchecked((long)word),
        var number => Convert.ToInt64(number, CultureInfo.InvariantCulture)
      });
    } catch (IrException) {
      return ConstantFact.Unknown;
    }
  }

}
