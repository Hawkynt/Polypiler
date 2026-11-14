using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize;

/// <summary>
/// What replaces a run of statements of one block.
/// </summary>
/// <param name="Count">How many statements, from the one the rule was asked about on, are replaced.</param>
/// <param name="Statements">The statements that stand in their place.</param>
public sealed record StatementRewrite(int Count, IReadOnlyList<IrStmt> Statements);

/// <summary>
/// One rewrite of the optimizer, named by its optimization number. A rule looks at one expression or
/// at the statements of a block from one position on, and either leaves them alone or returns what
/// replaces them. A rule that cannot show its exactness condition does not apply; it never fails a
/// translation.
/// </summary>
public abstract partial class IrRule {

  /// <summary>Creates a rule.</summary>
  /// <param name="id">The optimization number, <c>O</c> and four digits.</param>
  /// <param name="name">What the rule does, in a few words.</param>
  /// <exception cref="ArgumentException">The number is not of the form <c>O####</c>.</exception>
  protected IrRule(string id, string name) {
    if (!IsId(id))
      throw new ArgumentException($"'{id}' is not an optimization number of the form O####.", nameof(id));

    this.Id = id;
    this.Name = name;
  }

  /// <summary>The optimization number, for example <c>O0100</c>.</summary>
  public string Id { get; }

  /// <summary>What the rule does, as <c>--explain-opt</c> names it.</summary>
  public string Name { get; }

  /// <summary>Whether a text is an optimization number: <c>O</c> followed by four digits.</summary>
  /// <param name="text">The text.</param>
  /// <returns><see langword="true"/> for <c>O0100</c>, <see langword="false"/> for <c>O100</c> or <c>o0100</c>.</returns>
  public static bool IsId(string text) => IdPattern().IsMatch(text);

  /// <summary>Tries the rule on an expression whose operands the optimizer has already visited.</summary>
  /// <param name="expression">The expression.</param>
  /// <param name="context">The function and what is known about its variables.</param>
  /// <param name="replacement">What replaces the expression; of its type.</param>
  /// <returns><see langword="true"/> when the rule applies.</returns>
  public virtual bool TryApply(IrExpr expression, RuleContext context, [NotNullWhen(true)] out IrExpr? replacement) {
    replacement = null;
    return false;
  }

  /// <summary>Tries the rule on the statements of a block from one position on.</summary>
  /// <param name="statements">The statements of the block.</param>
  /// <param name="index">The position the rule is asked about.</param>
  /// <param name="context">The function, what is known about its variables, and the blocks around this one.</param>
  /// <param name="rewrite">What replaces statements from the position on.</param>
  /// <returns><see langword="true"/> when the rule applies.</returns>
  public virtual bool TryApply(IReadOnlyList<IrStmt> statements, int index, RuleContext context, [NotNullWhen(true)] out StatementRewrite? rewrite) {
    rewrite = null;
    return false;
  }

  /// <summary>The rule as a diagnostic names it.</summary>
  public override string ToString() => $"{this.Id} {this.Name}";

  [GeneratedRegex("^O[0-9]{4}$")]
  private static partial Regex IdPattern();

}
