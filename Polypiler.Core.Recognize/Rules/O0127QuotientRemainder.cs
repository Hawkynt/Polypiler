using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize.Rules;

/// <summary>
/// O0127: an integer remainder <c>a % b</c> that follows the quotient <c>q = a / b</c> of the same
/// operands in one block becomes <c>a - q * b</c>, where the target's capability table says that is
/// cheaper than a second division (<see cref="RuleTarget.RemainderFromQuotient"/>).
/// <para>
/// The quotient is assigned to a variable or declared with it; the remainder stands in a later
/// statement of the same block - in the expressions that statement evaluates itself, not inside a
/// block of it. In between, and before the remainder within its statement, nothing writes the
/// quotient's variable or a variable of the operands. Both operands have no effect and cannot trap.
/// The quotient has already trapped where the remainder would - on a zero divisor, and on the
/// smallest value divided by -1 - so the remainder computed from it, with wrapping arithmetic, is the
/// same everywhere it is reached. Where the table does not say so, both divisions stay as they are:
/// their operands are variables already, read once each.
/// </para>
/// <para>
/// JavaScript's frontend writes a quotient and a remainder under a coercion with their divisor tested
/// (#1367): <c>b == 0 ? 0 : b == -1 ? 0 - a : a / b</c> and <c>b == 0 ? 0 : b == -1 ? 0 : a % b</c>,
/// without the -1 test where <c>b</c> cannot be -1. Such a quotient is <c>a / b</c> for every <c>b</c>
/// but 0, with wrapping at -1, so <c>a - q * b</c> is the remainder wherever <c>b</c> is not 0: the
/// remainder is taken only inside the branch of a test of the same divisor against 0.
/// </para>
/// </summary>
public sealed class O0127QuotientRemainder : IrRule {

  /// <summary>Creates the rule.</summary>
  public O0127QuotientRemainder() : base("O0127", "remainder from the quotient of the same operands") { }

  /// <inheritdoc/>
  public override bool TryApply(IReadOnlyList<IrStmt> statements, int index, RuleContext context, [NotNullWhen(true)] out StatementRewrite? rewrite) {
    rewrite = null;
    if (!context.Target.RemainderFromQuotient)
      return false;

    var (quotient, computed) = statements[index] switch {
      IrDeclare { Initializer: { } value } declare => (declare.Local, value),
      IrAssign assign => (assign.Local, assign.Value),
      _ => (null, null)
    };
    var division = computed as IrBinary ?? Tested(computed);
    var tested = division is not null && !ReferenceEquals(division, computed);
    if (division is { Op: not IrBinaryOp.Divide })
      return false;

    if (quotient is null || division is null || !division.Type.IsWideInteger || quotient.Type != division.Type
      || !IrShape.IsTrapFree(division.Left) || !IrShape.IsTrapFree(division.Right))
      return false;

    var guarded = new HashSet<IrLocal>(IrShape.Locals(division.Left).Concat(IrShape.Locals(division.Right))) { quotient };
    if (IrShape.Reads(division.Left, quotient) || IrShape.Reads(division.Right, quotient))
      return false;

    bool Remainder(IrExpr node) => node is IrBinary { Op: IrBinaryOp.Remainder } remainder && IrShape.Same(remainder.Left, division.Left) && IrShape.Same(remainder.Right, division.Right);
    // A tested quotient is the quotient only where the divisor is not 0: the remainder is taken in the branch where it is not.
    bool Matches(IrExpr node) => tested ? NonZero(node, division.Right) is { } branch && IrShape.Nodes(branch).Any(Remainder) : Remainder(node);
    for (var position = index + 1; position < statements.Count; ++position) {
      var statement = statements[position];
      var own = IrStatements.Expressions(statement);
      if (own.SelectMany(IrShape.Nodes).Any(Matches)) {
        if (statement is not (IrDeclare or IrAssign or IrReturn or IrExprStmt or IrStore)
          || own.SelectMany(IrShape.Nodes).OfType<IrStep>().Any() || own.SelectMany(IrShape.Nodes).OfType<IrCall>().Any())
          return false;

        var from = new IrBinary(IrBinaryOp.Subtract, division.Left, new IrBinary(IrBinaryOp.Multiply, new IrLocalRef(quotient), division.Right));
        IrExpr? Rewritten(IrExpr node) => !Matches(node) ? null
          : tested ? ((IrConditional)node) with { WhenFalse = IrShape.Map(((IrConditional)node).WhenFalse, inner => Remainder(inner) ? from : null) }
          : from;
        var replaced = Replace(statement, Rewritten);
        rewrite = new(position - index + 1, [.. statements.Skip(index).Take(position - index), replaced]);
        return true;
      }

      if (IrStatements.Writes(statement).Any(guarded.Contains))
        return false;
    }

    return false;
  }

  /// <summary>
  /// The division of a quotient whose divisor is tested - <c>b == 0 ? 0 : (b == -1 ? 0 - a : a / b)</c>,
  /// or without the -1 test - or <see langword="null"/>.
  /// </summary>
  private static IrBinary? Tested(IrExpr? value) {
    if (value is not IrConditional { WhenTrue: IrConst { Value: 0 } } choice || NonZero(choice, null) is not { } rest)
      return null;

    var division = rest is IrConditional { Condition: IrBinary { Op: IrBinaryOp.Equal, Right: IrConst { Value: -1 } } minusOne, WhenTrue: IrBinary { Op: IrBinaryOp.Subtract, Left: IrConst { Value: 0 } } negated, WhenFalse: IrBinary inner }
      && IrShape.Same(minusOne.Left, inner.Right) && IrShape.Same(negated.Right, inner.Left) ? inner : rest as IrBinary;
    return division is { Op: IrBinaryOp.Divide } && NonZero(choice, division.Right) is not null ? division : null;
  }

  /// <summary>The branch of <c>d == 0 ? 0 : branch</c> for the divisor (any divisor where it is <see langword="null"/>), or <see langword="null"/>.</summary>
  private static IrExpr? NonZero(IrExpr node, IrExpr? divisor)
    => node is IrConditional { Condition: IrBinary { Op: IrBinaryOp.Equal, Left: var tested, Right: IrConst { Value: 0 } }, WhenTrue: IrConst { Value: 0 }, WhenFalse: var branch }
      && (divisor is null || IrShape.Same(tested, divisor)) ? branch : null;

  private static IrStmt Replace(IrStmt statement, System.Func<IrExpr, IrExpr?> replace) => statement switch {
    IrDeclare declare => declare with { Initializer = IrShape.Map(declare.Initializer!, replace) },
    IrAssign assign => assign with { Value = IrShape.Map(assign.Value, replace) },
    IrReturn returned => returned with { Value = IrShape.Map(returned.Value!, replace) },
    IrExprStmt expression => new IrExprStmt(IrShape.Map(expression.Expression, replace)),
    _ => new IrStore(IrShape.Map(((IrStore)statement).Array, replace), IrShape.Map(((IrStore)statement).Index, replace), IrShape.Map(((IrStore)statement).Value, replace))
  };

}
