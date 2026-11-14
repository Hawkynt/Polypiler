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
/// </summary>
public sealed class O0127QuotientRemainder : IrRule {

  /// <summary>Creates the rule.</summary>
  public O0127QuotientRemainder() : base("O0127", "remainder from the quotient of the same operands") { }

  /// <inheritdoc/>
  public override bool TryApply(IReadOnlyList<IrStmt> statements, int index, RuleContext context, [NotNullWhen(true)] out StatementRewrite? rewrite) {
    rewrite = null;
    if (!context.Target.RemainderFromQuotient)
      return false;

    var (quotient, division) = statements[index] switch {
      IrDeclare { Initializer: IrBinary { Op: IrBinaryOp.Divide } value } declare => (declare.Local, value),
      IrAssign { Value: IrBinary { Op: IrBinaryOp.Divide } value } assign => (assign.Local, value),
      _ => (null, null)
    };
    if (quotient is null || division is null || !division.Type.IsWideInteger || quotient.Type != division.Type
      || !IrShape.IsTrapFree(division.Left) || !IrShape.IsTrapFree(division.Right))
      return false;

    var guarded = new HashSet<IrLocal>(IrShape.Locals(division.Left).Concat(IrShape.Locals(division.Right))) { quotient };
    if (IrShape.Reads(division.Left, quotient) || IrShape.Reads(division.Right, quotient))
      return false;

    bool Matches(IrExpr node) => node is IrBinary { Op: IrBinaryOp.Remainder } remainder && IrShape.Same(remainder.Left, division.Left) && IrShape.Same(remainder.Right, division.Right);
    for (var position = index + 1; position < statements.Count; ++position) {
      var statement = statements[position];
      var own = IrStatements.Expressions(statement);
      if (own.SelectMany(IrShape.Nodes).Any(Matches)) {
        if (statement is not (IrDeclare or IrAssign or IrReturn or IrExprStmt or IrStore)
          || own.SelectMany(IrShape.Nodes).OfType<IrStep>().Any() || own.SelectMany(IrShape.Nodes).OfType<IrCall>().Any())
          return false;

        var from = new IrBinary(IrBinaryOp.Subtract, division.Left, new IrBinary(IrBinaryOp.Multiply, new IrLocalRef(quotient), division.Right));
        var replaced = Replace(statement, node => Matches(node) ? from : null);
        rewrite = new(position - index + 1, [.. statements.Skip(index).Take(position - index), replaced]);
        return true;
      }

      if (IrStatements.Writes(statement).Any(guarded.Contains))
        return false;
    }

    return false;
  }

  private static IrStmt Replace(IrStmt statement, System.Func<IrExpr, IrExpr?> replace) => statement switch {
    IrDeclare declare => declare with { Initializer = IrShape.Map(declare.Initializer!, replace) },
    IrAssign assign => assign with { Value = IrShape.Map(assign.Value, replace) },
    IrReturn returned => returned with { Value = IrShape.Map(returned.Value!, replace) },
    IrExprStmt expression => new IrExprStmt(IrShape.Map(expression.Expression, replace)),
    _ => new IrStore(IrShape.Map(((IrStore)statement).Array, replace), IrShape.Map(((IrStore)statement).Index, replace), IrShape.Map(((IrStore)statement).Value, replace))
  };

}
