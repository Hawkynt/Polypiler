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
    IrNew creation => creation.Arguments,
    IrMethodCall { Target: { } target } call => [target, .. call.Arguments],
    IrMethodCall call => call.Arguments,
    IrReferenceEquals equality => [equality.Left, equality.Right],
    IrTypeTest test => [test.Value],
    IrCast cast => [cast.Value],
    IrAsCast cast => [cast.Value],
    IrSome some => [some.Value],
    IrIsNone test => [test.Value],
    IrUnwrap unwrap => [unwrap.Value],
    IrCoalesce coalesce => [coalesce.Value, coalesce.Fallback],
    IrIsNull test => [test.Value],
    IrBind bind => [bind.Value],
    IrGenericEquals equality => [equality.Left, equality.Right],
    IrGenericCompare order => [order.Left, order.Right],
    IrThrowExpression thrown => [thrown.Exception],
    IrCollection operation => operation.Arguments,
    IrInvoke invocation => [invocation.Callee, .. invocation.Arguments],
    IrBoundMethod { Receiver: { } receiver } => [receiver],
    IrArraySequence sequence => [sequence.Array],
    IrSequence operation => operation.Arguments,
    IrAwait awaited => [awaited.Operand],
    IrAsync operation => operation.Arguments,
    IrForeignCall call => call.Arguments,
    IrVariantOf { Value: { } value } => [value],
    IrVariantTagOf tag => [tag.Value],
    IrVariantValue value => [value.Value],
    IrUnionOf made => [made.Value],
    IrUnionTagOf tag => [tag.Value],
    IrUnionValue value => [value.Value],
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
    IrWriteLine write => [write.Value],
    IrIf conditional => [conditional.Condition],
    IrWhile loop => [loop.Condition],
    IrDoWhile loop => [loop.Condition],
    IrFor loop => [loop.Condition],
    IrSwitch choice => [choice.Value],
    IrReturn { Value: { } value } => [value],
    IrThrow thrown => [thrown.Exception],
    IrTry attempt => [.. attempt.Catches.Select(clause => clause.Filter).OfType<IrExpr>()],
    IrUsing scope => [scope.Initializer],
    IrForEachCollection loop => [loop.Collection],
    IrYield produced => [produced.Value],
    IrYieldFrom delegated => [delegated.Sequence],
    IrForEachSequence loop => [loop.Sequence],
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
    IrTry attempt => [attempt.Body, .. attempt.Catches.Select(clause => clause.Body), .. attempt.Finally is { } final ? [final] : (IrBlock[])[]],
    IrUsing scope => [scope.Body],
    IrForEachCollection loop => [loop.Body],
    IrForEachSequence loop => [loop.Body],
    _ => []
  };

  /// <summary>An expression with new operands, in the order <see cref="Operands"/> lists them.</summary>
  /// <param name="expression">The expression.</param>
  /// <param name="operands">The new operands, as many as it has.</param>
  /// <returns>The expression rebuilt; one without operands as it is.</returns>
  /// <remarks>An expression rebuilt keeps the <see cref="IrExpr.Origins"/> of the one it replaces.</remarks>
  public static IrExpr With(IrExpr expression, IReadOnlyList<IrExpr> operands) {
    var rebuilt = Rebuilt(expression, operands);
    return expression.Origins.Count == 0 || ReferenceEquals(rebuilt.Origins, expression.Origins) ? rebuilt : rebuilt with { Origins = expression.Origins };
  }

  private static IrExpr Rebuilt(IrExpr expression, IReadOnlyList<IrExpr> operands) => expression switch {
    IrUnary unary => new IrUnary(unary.Op, operands[0]),
    IrBinary binary => new IrBinary(binary.Op, operands[0], operands[1]),
    IrConvert conversion => conversion with { Operand = operands[0] },
    IrConditional => new IrConditional(operands[0], operands[1], operands[2]),
    IrArrayLoad => new IrArrayLoad(operands[0], operands[1]),
    IrArrayLength => new IrArrayLength(operands[0]),
    IrNewArray creation => creation with { Length = operands[0] },
    IrCall call => call with { Arguments = operands },
    IrIntrinsic intrinsic => intrinsic with { Arguments = operands },
    IrStep step => step with { Target = operands[0] },
    IrStructNew creation => creation with { Arguments = operands },
    IrFieldLoad load => load with { Target = operands[0] },
    IrStructEquals => new IrStructEquals(operands[0], operands[1]),
    IrNew creation => creation with { Arguments = operands },
    IrMethodCall { Target: not null } call => call with { Target = operands[0], Arguments = [.. operands.Skip(1)] },
    IrMethodCall call => call with { Arguments = operands },
    IrReferenceEquals => new IrReferenceEquals(operands[0], operands[1]),
    IrTypeTest test => test with { Value = operands[0] },
    IrCast cast => cast with { Value = operands[0] },
    IrAsCast cast => cast with { Value = operands[0] },
    IrSome => new IrSome(operands[0]),
    IrIsNone => new IrIsNone(operands[0]),
    IrUnwrap unwrap => unwrap with { Value = operands[0] },
    IrCoalesce => new IrCoalesce(operands[0], operands[1]),
    IrIsNull => new IrIsNull(operands[0]),
    IrBind bind => bind with { Value = operands[0] },
    IrGenericEquals => new IrGenericEquals(operands[0], operands[1]),
    IrGenericCompare => new IrGenericCompare(operands[0], operands[1]),
    IrThrowExpression thrown => thrown with { Exception = operands[0] },
    IrCollection operation => operation with { Arguments = operands },
    IrInvoke invocation => invocation with { Callee = operands[0], Arguments = [.. operands.Skip(1)] },
    IrBoundMethod { Receiver: not null } bound => bound with { Receiver = operands[0] },
    IrArraySequence => new IrArraySequence(operands[0]),
    IrSequence operation => operation with { Arguments = operands },
    IrAwait => new IrAwait(operands[0]),
    IrAsync operation => operation with { Arguments = operands },
    IrForeignCall call => call with { Arguments = operands },
    IrVariantOf { Value: not null } made => made with { Value = operands[0] },
    IrVariantTagOf => new IrVariantTagOf(operands[0]),
    IrVariantValue value => value with { Value = operands[0] },
    IrUnionOf made => made with { Value = operands[0] },
    IrUnionTagOf => new IrUnionTagOf(operands[0]),
    IrUnionValue value => new IrUnionValue(operands[0], value.Member) { IsTested = value.IsTested },
    _ => expression
  };

  /// <summary>
  /// A statement other than a block with new expressions and blocks, in the orders
  /// <see cref="Expressions"/> and <see cref="Blocks"/> list them.
  /// </summary>
  /// <param name="statement">The statement.</param>
  /// <param name="expressions">The new expressions, as many as it evaluates itself.</param>
  /// <param name="blocks">The new blocks, as many as it holds.</param>
  /// <returns>The statement rebuilt; one without either as it is.</returns>
  public static IrStmt With(IrStmt statement, IReadOnlyList<IrExpr> expressions, IReadOnlyList<IrBlock> blocks) {
    switch (statement) {
      case IrDeclare { Initializer: not null } declare:
        return declare with { Initializer = expressions[0] };
      case IrAssign assign:
        return assign with { Value = expressions[0] };
      case IrGlobalAssign assign:
        return assign with { Value = expressions[0] };
      case IrStore store:
        return store with { Array = expressions[0], Index = expressions[1], Value = expressions[2] };
      case IrFieldStore store:
        return store with { Target = expressions[0], Value = expressions[1] };
      case IrExprStmt evaluated:
        return evaluated with { Expression = expressions[0] };
      case IrWriteLine write:
        return write with { Value = expressions[0] };
      case IrIf conditional:
        return conditional with { Condition = expressions[0], Then = blocks[0], Else = blocks.Count > 1 ? blocks[1] : null };
      case IrWhile loop:
        return loop with { Condition = expressions[0], Body = blocks[0] };
      case IrDoWhile loop:
        return loop with { Body = blocks[0], Condition = expressions[0] };
      case IrFor loop:
        return loop with { Condition = expressions[0], Body = blocks[0], Step = blocks[1] };
      case IrSwitch choice:
        return choice with { Value = expressions[0], Sections = [.. choice.Sections.Select((section, index) => section with { Body = blocks[index] })] };
      case IrReturn { Value: not null } result:
        return result with { Value = expressions[0] };
      case IrThrow thrown:
        return thrown with { Exception = expressions[0] };
      case IrTry attempt:
        var filter = 0;
        var catches = attempt.Catches.Select((clause, index) => clause with { Filter = clause.Filter is null ? null : expressions[filter++], Body = blocks[index + 1] }).ToList();
        return attempt with { Body = blocks[0], Catches = catches, Finally = attempt.Finally is null ? null : blocks[^1] };
      case IrUsing scope:
        return scope with { Initializer = expressions[0], Body = blocks[0] };
      case IrForEachCollection loop:
        return loop with { Collection = expressions[0], Body = blocks[0] };
      case IrYield produced:
        return produced with { Value = expressions[0] };
      case IrYieldFrom delegated:
        return delegated with { Sequence = expressions[0] };
      case IrForEachSequence loop:
        return loop with { Sequence = expressions[0], Body = blocks[0] };
      default:
        return statement;
    }
  }

  /// <summary>Every node of an expression, the expression first; the nodes of the body of a lambda are among them.</summary>
  /// <param name="expression">The expression.</param>
  /// <returns>The nodes in pre-order.</returns>
  public static IEnumerable<IrExpr> Nodes(IrExpr expression) {
    // A stack rather than nested enumerators, which would pass every node up through each level above it.
    var pending = new Stack<IrExpr>();
    pending.Push(expression);
    while (pending.Count > 0) {
      var node = pending.Pop();
      yield return node;
      if (node is IrLambda lambda) {
        foreach (var inner in Nodes(lambda.Body))
          yield return inner;

        continue;
      }

      var operands = Operands(node);
      for (var index = operands.Count - 1; index >= 0; --index)
        pending.Push(operands[index]);
    }
  }

  /// <summary>Every expression node within a statement, those of its blocks included.</summary>
  /// <param name="statement">The statement.</param>
  /// <returns>The nodes, statement by statement in pre-order.</returns>
  public static IEnumerable<IrExpr> Nodes(IrStmt statement) {
    var inner = statement is IrBlock block ? block.Statements : Blocks(statement).SelectMany(nested => nested.Statements);
    return Expressions(statement).SelectMany(Nodes).Concat(inner.SelectMany(Nodes));
  }

}
