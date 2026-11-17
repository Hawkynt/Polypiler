using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>
/// Resolves the layout of a module for one target (#110): every <see cref="IrLayoutOf"/> becomes its value under
/// the target's <see cref="LayoutProfile"/>, and every <see cref="IrFieldAddress"/> the pointer arithmetic it
/// stands for - the base moved by the field's offset (<see cref="IrIntrinsicOp.PointerAdd"/> on the <c>u8*</c>)
/// and cast to the field's pointer type (<see cref="IrIntrinsicOp.PointerCast"/>). What is left are nodes every
/// backend that has pointers writes already, so a backend needs nothing of its own for records in memory.
/// </summary>
public static class IrLayoutLowering {

  /// <summary>The module with its layouts resolved for a profile.</summary>
  /// <param name="module">The module, which the verifier accepted.</param>
  /// <param name="profile">The profile of the target.</param>
  /// <returns>A module without layout queries and field addresses; the module itself when it has none.</returns>
  /// <exception cref="ArgumentException">A record laid out has a problem under the profile.</exception>
  public static IrModule Lower(IrModule module, LayoutProfile profile) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(profile);
    var rewriter = new Rewriter(profile);
    var functions = module.Functions.Select(function => function with { Body = rewriter.Block(function.Body) }).ToList();
    return rewriter.Changed ? module with { Functions = functions } : module;
  }

  /// <summary>A statement with its expressions and blocks mapped.</summary>
  /// <param name="statement">The statement.</param>
  /// <param name="expression">What each expression the statement evaluates itself becomes.</param>
  /// <param name="block">What each block it holds becomes.</param>
  /// <returns>The statement rebuilt.</returns>
  public static IrStmt Map(IrStmt statement, Func<IrExpr, IrExpr> expression, Func<IrBlock, IrBlock> block) => statement switch {
    IrBlock inner => block(inner),
    IrDeclare declare => declare with { Initializer = declare.Initializer is null ? null : expression(declare.Initializer) },
    IrAssign assign => assign with { Value = expression(assign.Value) },
    IrGlobalAssign assign => assign with { Value = expression(assign.Value) },
    IrStore store => new IrStore(expression(store.Array), expression(store.Index), expression(store.Value)),
    IrFieldStore store => store with { Target = expression(store.Target), Value = expression(store.Value) },
    IrExprStmt evaluated => new IrExprStmt(expression(evaluated.Expression)),
    IrIf conditional => new IrIf(expression(conditional.Condition), block(conditional.Then), conditional.Else is null ? null : block(conditional.Else)),
    IrWhile loop => new IrWhile(expression(loop.Condition), block(loop.Body)),
    IrDoWhile loop => new IrDoWhile(block(loop.Body), expression(loop.Condition)),
    IrFor loop => new IrFor(expression(loop.Condition), block(loop.Body), block(loop.Step)) { PerIteration = loop.PerIteration },
    IrSwitch choice => new IrSwitch(expression(choice.Value), [.. choice.Sections.Select(section => section with { Body = block(section.Body) })]),
    IrReturn result => new IrReturn(result.Value is null ? null : expression(result.Value)),
    IrThrow thrown => new IrThrow(expression(thrown.Exception)),
    IrTry attempt => new IrTry(block(attempt.Body),
      [.. attempt.Catches.Select(clause => clause with { Filter = clause.Filter is null ? null : expression(clause.Filter), Body = block(clause.Body) })],
      attempt.Finally is null ? null : block(attempt.Finally)),
    IrUsing scope => scope with { Initializer = expression(scope.Initializer), Body = block(scope.Body) },
    IrForEachCollection loop => loop with { Collection = expression(loop.Collection), Body = block(loop.Body) },
    IrYield produced => new IrYield(expression(produced.Value)),
    IrYieldFrom delegated => new IrYieldFrom(expression(delegated.Sequence)),
    IrForEachSequence loop => loop with { Sequence = expression(loop.Sequence), Body = block(loop.Body) },
    _ => statement
  };

  private sealed class Rewriter(LayoutProfile profile) {

    public bool Changed { get; private set; }

    public IrBlock Block(IrBlock block) => new([.. block.Statements.Select(statement => Map(statement, this.Expression, this.Block))]);

    private IrExpr Expression(IrExpr expression) {
      if (expression is IrLambda lambda)
        return lambda with { Body = this.Block(lambda.Body) };

      var operands = IrTree.Operands(expression);
      var lowered = operands.Select(this.Expression).ToList();
      if (!operands.Zip(lowered).All(pair => ReferenceEquals(pair.First, pair.Second)))
        expression = IrTree.With(expression, lowered);

      switch (expression) {
        case IrLayoutOf query:
          this.Changed = true;
          return new IrConst(IrType.I64, RecordLayout.Answer(query.Query, query.Of, query.Field, profile));
        case IrFieldAddress address:
          this.Changed = true;
          var offset = RecordLayout.OffsetOf(address.Record, address.Field, profile);
          var moved = offset == 0 ? address.Base : new IrIntrinsic(IrIntrinsicOp.PointerAdd, address.Base.Type, [address.Base, new IrConst(IrType.I32, offset)]);
          return address.Type == moved.Type ? moved : new IrIntrinsic(IrIntrinsicOp.PointerCast, address.Type, [moved]);
        default:
          return expression;
      }
    }

  }

}
