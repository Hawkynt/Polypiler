using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize.Rules;

/// <summary>
/// A rule that recognizes an expression; it may also look for the same value composed by a run of
/// assignments (see <see cref="Windows"/>).
/// </summary>
public abstract class ExpressionRule : IrRule {

  /// <summary>Creates the rule.</summary>
  /// <param name="id">The optimization number.</param>
  /// <param name="name">What the rule does.</param>
  /// <param name="runs">Whether the rule also recognizes runs of assignments to one variable.</param>
  protected ExpressionRule(string id, string name, bool runs = false) : base(id, name) => this.Runs = runs;

  /// <summary>Whether the rule also recognizes runs of assignments to one variable.</summary>
  public bool Runs { get; }

  /// <inheritdoc/>
  public sealed override bool TryApply(IrExpr expression, RuleContext context, [NotNullWhen(true)] out IrExpr? replacement) {
    replacement = this.Recognize(expression, context);
    return replacement is not null;
  }

  /// <inheritdoc/>
  public sealed override bool TryApply(IReadOnlyList<IrStmt> statements, int index, RuleContext context, [NotNullWhen(true)] out StatementRewrite? rewrite) {
    rewrite = null;
    return this.Runs && Windows.TryRun(statements, index, context, expression => this.Recognize(expression, context), out rewrite)
      || this.TryStatements(statements, index, context, out rewrite);
  }

  /// <summary>Recognizes a statement form of the rule other than a run of assignments, such as a loop.</summary>
  /// <param name="statements">The statements of the block.</param>
  /// <param name="index">The position the rule is asked about.</param>
  /// <param name="context">The facts of the function.</param>
  /// <param name="rewrite">What replaces statements from the position on.</param>
  /// <returns><see langword="true"/> when the rule applies.</returns>
  protected virtual bool TryStatements(IReadOnlyList<IrStmt> statements, int index, RuleContext context, [NotNullWhen(true)] out StatementRewrite? rewrite) {
    rewrite = null;
    return false;
  }

  /// <summary>The replacement of an expression, of its type, or <see langword="null"/> when the rule does not apply.</summary>
  /// <param name="expression">The expression, its operands already visited.</param>
  /// <param name="context">The facts of the function.</param>
  /// <returns>The replacement.</returns>
  protected abstract IrExpr? Recognize(IrExpr expression, RuleContext context);

}
