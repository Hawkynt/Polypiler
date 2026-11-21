using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>
/// The rules by which a rewrite carries origins: a replacement takes the origins of what it replaces,
/// in the order of the replaced nodes; a node a pass made for nothing of the source says so with
/// <see cref="Synthetic"/>; a block split in two leaves both halves the origins of the block. A
/// missing origin is never taken from a neighbour.
/// </summary>
public static class IrOrigins {

  /// <summary>The origins of several statements, in their order, each kept.</summary>
  /// <param name="replaced">The statements.</param>
  /// <returns>Their origins, one after another.</returns>
  public static IReadOnlyList<Origin> Of(params IEnumerable<IrStmt> replaced) => [.. replaced.SelectMany(statement => statement.Origins)];

  /// <summary>The origins of several expressions, in their order, each kept.</summary>
  /// <param name="replaced">The expressions.</param>
  /// <returns>Their origins, one after another.</returns>
  public static IReadOnlyList<Origin> Of(params IEnumerable<IrExpr> replaced) => [.. replaced.SelectMany(expression => expression.Origins)];

  /// <summary>A statement that replaces others: it carries their origins, in their order, instead of its own.</summary>
  /// <typeparam name="T">The kind of the replacement.</typeparam>
  /// <param name="replacement">The new statement.</param>
  /// <param name="replaced">The statements it replaces; one for a replacement, several for a merge.</param>
  /// <returns>The replacement with the origins.</returns>
  public static T Replace<T>(T replacement, params IEnumerable<IrStmt> replaced) where T : IrStmt
    => (T)((IrStmt)replacement with { Origins = Of(replaced) });

  /// <summary>An expression that replaces others: it carries their origins, in their order, instead of its own.</summary>
  /// <typeparam name="T">The kind of the replacement.</typeparam>
  /// <param name="replacement">The new expression.</param>
  /// <param name="replaced">The expressions it replaces; one for a replacement, several for a merge.</param>
  /// <returns>The replacement with the origins.</returns>
  public static T Replace<T>(T replacement, params IEnumerable<IrExpr> replaced) where T : IrExpr
    => (T)((IrExpr)replacement with { Origins = Of(replaced) });

  /// <summary>A statement a pass made, marked with the pass after the origins it has.</summary>
  /// <param name="statement">The statement.</param>
  /// <param name="passId">The ID of the pass, such as <c>O0100</c>.</param>
  /// <returns>The statement with <see cref="Synthetic"/> added.</returns>
  public static IrStmt MarkSynthetic(IrStmt statement, string passId) => statement with { Origins = [.. statement.Origins, new Synthetic(passId)] };

  /// <summary>An expression a pass made, marked with the pass after the origins it has.</summary>
  /// <param name="expression">The expression.</param>
  /// <param name="passId">The ID of the pass, such as <c>O0100</c>.</param>
  /// <returns>The expression with <see cref="Synthetic"/> added.</returns>
  public static IrExpr MarkSynthetic(IrExpr expression, string passId) => expression with { Origins = [.. expression.Origins, new Synthetic(passId)] };

  /// <summary>A block split before one of its statements: both halves keep the origins of the block.</summary>
  /// <param name="block">The block.</param>
  /// <param name="index">The index of the first statement of the second half, 0 to the number of statements.</param>
  /// <returns>The statements before the index and those from it on, each a block.</returns>
  public static (IrBlock First, IrBlock Second) Split(IrBlock block, int index)
    => (block with { Statements = [.. block.Statements.Take(index)] }, block with { Statements = [.. block.Statements.Skip(index)] });

  /// <summary>
  /// The instructions of a function - every statement that is no block, those in the bodies of its
  /// lambdas included - in the order they stand in the source, numbered from 1.
  /// </summary>
  /// <param name="function">The function.</param>
  /// <returns>The instructions with their numbers.</returns>
  public static IEnumerable<(int Number, IrStmt Statement)> Instructions(IrFunction function)
    => Walk(function.Body, new HashSet<IrLambda>(ReferenceEqualityComparer.Instance)).Select((statement, index) => (index + 1, statement));

  private static IEnumerable<IrStmt> Walk(IrStmt statement, HashSet<IrLambda> lambdas) {
    if (statement is not IrBlock)
      yield return statement;

    foreach (var lambda in IrTree.Expressions(statement).SelectMany(IrTree.Nodes).OfType<IrLambda>())
      if (lambdas.Add(lambda))
        foreach (var inner in Walk(lambda.Body, lambdas))
          yield return inner;

    var nested = statement is IrBlock block ? block.Statements : IrTree.Blocks(statement);
    foreach (var inner in nested.SelectMany(child => Walk(child, lambdas)))
      yield return inner;
  }

}
