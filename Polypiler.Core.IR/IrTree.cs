using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>The parts of the IR's trees, for a pass that has to see every node of a function.</summary>
public static class IrTree {

  /// <summary>The operands of an expression, in the order they are evaluated.</summary>
  /// <param name="expression">The expression.</param>
  /// <returns>Its operands; empty for a constant, a variable, a table and a default value.</returns>
  public static IReadOnlyList<IrExpr> Operands(IrExpr expression) => expression switch {
    IrUnary unary => [unary.Operand],
    IrBinary binary => [binary.Left, binary.Right],
    IrConvert conversion => [conversion.Operand],
    IrConditional choice => [choice.Condition, choice.WhenTrue, choice.WhenFalse],
    IrArrayLoad load => [load.Array, load.Index],
    IrArrayLength length => [length.Array],
    IrNewArray creation => [creation.Length],
    IrCall call => call.Arguments,
    IrIntrinsic intrinsic => intrinsic.Arguments,
    IrStep step => [step.Target],
    IrStructNew creation => creation.Arguments,
    IrFieldLoad load => [load.Target],
    IrStructEquals equality => [equality.Left, equality.Right],
    _ => []
  };

  /// <summary>The expressions a statement evaluates itself, without those of the blocks it holds.</summary>
  /// <param name="statement">The statement.</param>
  /// <returns>The expressions, in order.</returns>
  public static IReadOnlyList<IrExpr> Expressions(IrStmt statement) => statement switch {
    IrDeclare { Initializer: { } value } => [value],
    IrAssign assign => [assign.Value],
    IrGlobalAssign assign => [assign.Value],
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

  /// <summary>The blocks a statement holds directly; a block is its own.</summary>
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

  /// <summary>Every node of an expression, the expression first.</summary>
  /// <param name="expression">The expression.</param>
  /// <returns>The nodes in pre-order.</returns>
  public static IEnumerable<IrExpr> Nodes(IrExpr expression) => Operands(expression).SelectMany(Nodes).Prepend(expression);

  /// <summary>Every expression node within a statement, those of its blocks included.</summary>
  /// <param name="statement">The statement.</param>
  /// <returns>The nodes, statement by statement in pre-order.</returns>
  public static IEnumerable<IrExpr> Nodes(IrStmt statement) {
    var inner = statement is IrBlock block ? block.Statements : Blocks(statement).SelectMany(nested => nested.Statements);
    return Expressions(statement).SelectMany(Nodes).Concat(inner.SelectMany(Nodes));
  }

}
