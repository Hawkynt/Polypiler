using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize;

/// <summary>The parts of a statement a rule looks into: its own expressions, its blocks, and what it writes.</summary>
public static class IrStatements {

  /// <summary>The expressions a statement evaluates itself, in order, without those of its blocks.</summary>
  /// <param name="statement">The statement.</param>
  /// <returns>The expressions.</returns>
  public static IReadOnlyList<IrExpr> Expressions(IrStmt statement) => statement switch {
    IrDeclare { Initializer: { } value } => [value],
    IrAssign assign => [assign.Value],
    IrStore store => [store.Array, store.Index, store.Value],
    IrFieldStore store => [store.Target, store.Value],
    IrExprStmt expression => [expression.Expression],
    IrIf conditional => [conditional.Condition],
    IrWhile loop => [loop.Condition],
    IrDoWhile loop => [loop.Condition],
    IrFor loop => [loop.Condition],
    IrSwitch choice => [choice.Value],
    IrReturn { Value: { } value } => [value],
    _ => []
  };

  /// <summary>The blocks a statement holds directly.</summary>
  /// <param name="statement">The statement.</param>
  /// <returns>The blocks, in the order they appear.</returns>
  public static IReadOnlyList<IrBlock> Blocks(IrStmt statement) => statement switch {
    IrBlock block => [block],
    IrIf { Else: { } otherwise } conditional => [conditional.Then, otherwise],
    IrIf conditional => [conditional.Then],
    IrWhile loop => [loop.Body],
    IrDoWhile loop => [loop.Body],
    IrFor loop => [loop.Body, loop.Step],
    IrSwitch choice => [.. choice.Sections.Select(section => section.Body)],
    _ => []
  };

  /// <summary>Every statement within a statement, the statement first.</summary>
  /// <param name="statement">The statement.</param>
  /// <returns>The statements in pre-order.</returns>
  public static IEnumerable<IrStmt> All(IrStmt statement) {
    yield return statement;
    var inner = statement is IrBlock block ? block.Statements : Blocks(statement).SelectMany(nested => nested.Statements);
    foreach (var nested in inner.SelectMany(All))
      yield return nested;
  }

  /// <summary>Every expression a statement evaluates, those of its blocks included.</summary>
  /// <param name="statement">The statement.</param>
  /// <returns>The top-level expressions.</returns>
  public static IEnumerable<IrExpr> AllExpressions(IrStmt statement) => All(statement).SelectMany(Expressions);

  /// <summary>Whether a statement reads or writes a variable anywhere.</summary>
  /// <param name="statement">The statement.</param>
  /// <param name="local">The variable.</param>
  /// <returns><see langword="true"/> when it names the variable.</returns>
  public static bool Mentions(IrStmt statement, IrLocal local)
    => All(statement).Any(inner => inner is IrAssign assign && assign.Local == local || inner is IrDeclare declare && declare.Local == local)
      || AllExpressions(statement).Any(expression => IrShape.Reads(expression, local));

  /// <summary>The variables a statement writes anywhere: by assignment, declaration, step, or a store into one of their fields.</summary>
  /// <param name="statement">The statement.</param>
  /// <returns>Each variable once.</returns>
  public static IReadOnlyCollection<IrLocal> Writes(IrStmt statement) {
    var written = new List<IrLocal>();
    foreach (var inner in All(statement))
      if (inner is IrAssign assign)
        written.Add(assign.Local);
      else if (inner is IrDeclare declare)
        written.Add(declare.Local);
      else if (inner is IrFieldStore { Target: var target } && Root(target) is { } root)
        written.Add(root);

    written.AddRange(AllExpressions(statement).SelectMany(IrShape.Nodes).OfType<IrStep>()
      .Select(step => step.Target).OfType<IrLocalRef>().Select(reference => reference.Local));
    return written.Distinct().ToList();
  }

  /// <summary>
  /// The variable a place of a field store is a part of: the variable itself, or the one whose field
  /// holds the place; <see langword="null"/> for an array element, whose array is shared by reference.
  /// </summary>
  /// <param name="place">The target of a field store.</param>
  /// <returns>The variable, or <see langword="null"/>.</returns>
  public static IrLocal? Root(IrExpr place) => place switch {
    IrLocalRef reference => reference.Local,
    IrFieldLoad load => Root(load.Target),
    _ => null
  };

  /// <summary>Whether a statement may leave its block other than by running to its end: a break, a continue or a return anywhere in it.</summary>
  /// <param name="statement">The statement.</param>
  /// <returns><see langword="true"/> when it holds a jump.</returns>
  public static bool Jumps(IrStmt statement) => All(statement).Any(inner => inner is IrBreak or IrContinue or IrReturn);

}
