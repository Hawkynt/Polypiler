using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust.Emit;

/// <summary>
/// The assignments inside expressions (<see cref="IrBind"/>) that a frontend writes to evaluate an
/// operand once, made statements of their own where that keeps the order of evaluation: a bind that is
/// evaluated before anything with an effect or a trap in its statement is an assignment before the
/// statement, its place in the expression the variable. A choice whose two branches begin with the same
/// bind - the operands of a lifted operator, evaluated whichever way the test went - gives the bind once,
/// after its condition. Every other bind stays where it is. Rust has assignments only as statements of
/// value <c>()</c>; a bind left inside an expression is a block, which clippy dislikes in a condition.
/// </summary>
internal static class BindHoisting {

  /// <summary>The module with the binds of every function's statements hoisted where they may be.</summary>
  public static IrModule Run(IrModule module) {
    if (!module.Functions.SelectMany(function => IrTree.Nodes(function.Body)).Any(node => node is IrBind))
      return module;

    return module with { Functions = [.. module.Functions.Select(function => function with { Body = Block(function.Body) })] };
  }

  private static IrBlock Block(IrBlock block) {
    var statements = new List<IrStmt>();
    foreach (var statement in block.Statements)
      statements.AddRange(Statement(statement));

    return new IrBlock(statements);
  }

  /// <summary>A statement, preceded by the binds hoisted out of the expression it evaluates first.</summary>
  private static IEnumerable<IrStmt> Statement(IrStmt statement) {
    var blocks = IrTree.Blocks(statement).Select(Block).ToList();
    var expressions = IrTree.Expressions(statement);
    var hoister = new Hoister();
    var first = statement switch {
      IrDeclare or IrAssign or IrReturn or IrExprStmt or IrIf or IrSwitch or IrThrow when expressions.Count > 0 => hoister.Visit(expressions[0]),
      _ => null
    };

    var rebuilt = statement is IrBlock ? new IrBlock(blocks)
      : IrTree.With(statement, first is null ? expressions : [first, .. expressions.Skip(1)], blocks);
    return [.. hoister.Before, rebuilt];
  }

  /// <summary>Walks an expression in the order it is evaluated, hoisting binds while nothing before them had an effect.</summary>
  private sealed class Hoister {

    public List<IrStmt> Before { get; } = [];

    private bool _front = true;

    public IrExpr Visit(IrExpr expression) {
      if (!this._front)
        return expression;

      switch (expression) {
        case IrLambda:
          return expression;
        case IrBind bind: {
          // Everything evaluated before the bind is pure or hoisted: the bind moves with all of its value,
          // whose own effects keep their order, and leaves the variable in its place.
          var value = this.Visit(bind.Value);
          this.Before.Add(new IrAssign(bind.Local, value));
          this._front = true;
          return new IrLocalRef(bind.Local);
        }
        case IrConditional choice: {
          var condition = this.Visit(choice.Condition);
          if (!this._front || !Pure(condition))
            return this.Stop(choice with { Condition = condition });

          // Two branches that begin with the same binds: those binds are evaluated either way, after the condition.
          var whenTrue = new Hoister();
          var whenFalse = new Hoister();
          var yes = whenTrue.Visit(choice.WhenTrue);
          var no = whenFalse.Visit(choice.WhenFalse);
          if (whenTrue.Before.Count == 0 && whenFalse.Before.Count == 0)
            return ReferenceEquals(condition, choice.Condition) ? choice : choice with { Condition = condition };
          if (!whenTrue.Before.SequenceEqual(whenFalse.Before))
            return this.Stop(choice with { Condition = condition });

          this.Before.AddRange(whenTrue.Before);
          this._front = false;
          return choice with { Condition = condition, WhenTrue = yes, WhenFalse = no };
        }
        case IrBinary { Op: IrBinaryOp.LogicalAnd or IrBinaryOp.LogicalOr } lazy: {
          // The right side may not be evaluated: nothing in it moves.
          var left = this.Visit(lazy.Left);
          this._front &= Pure(lazy.Right);
          return ReferenceEquals(left, lazy.Left) ? lazy : lazy with { Left = left };
        }
        case IrCoalesce coalesce: {
          // Nor may the fallback of ??.
          var value = this.Visit(coalesce.Value);
          this._front &= Pure(coalesce.Fallback);
          return ReferenceEquals(value, coalesce.Value) ? coalesce : coalesce with { Value = value };
        }
        default: {
          var operands = IrTree.Operands(expression);
          if (operands.Count == 0)
            return expression;

          var visited = new List<IrExpr>();
          foreach (var operand in operands) {
            var rewritten = this.Visit(operand);
            visited.Add(rewritten);
            if (!Pure(rewritten))
              this._front = false;
          }

          return visited.SequenceEqual(operands, ReferenceEqualityComparer.Instance) ? expression : IrTree.With(expression, visited);
        }
      }
    }

    private IrExpr Stop(IrExpr expression) {
      this._front = false;
      return expression;
    }

  }

  /// <summary>Whether evaluating an expression has no effect and cannot trap, so that a later bind may be evaluated before it.</summary>
  public static bool Pure(IrExpr expression) => expression switch {
    IrLocalRef or IrConst or IrConstRef or IrNone or IrNull => true,
    IrSome some => Pure(some.Value),
    IrIsNone test => Pure(test.Value),
    IrIsNull test => Pure(test.Value),
    IrUnwrap { IsTested: true } unwrap => Pure(unwrap.Value),
    IrUnary { Op: IrUnaryOp.LogicalNot or IrUnaryOp.BitNot or IrUnaryOp.Negate } unary => Pure(unary.Operand),
    IrBinary { Op: not (IrBinaryOp.Divide or IrBinaryOp.Remainder or IrBinaryOp.AddChecked or IrBinaryOp.SubtractChecked or IrBinaryOp.MultiplyChecked) } binary
      => Pure(binary.Left) && Pure(binary.Right),
    IrConditional choice => Pure(choice.Condition) && Pure(choice.WhenTrue) && Pure(choice.WhenFalse),
    _ => false
  };

}
