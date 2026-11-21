using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust.Emit;

/// <summary>
/// A new list filled by the statements right after its declaration, made of its elements at once
/// (#1422): <c>var l = new List&lt;T&gt;(); l.Add(a); l.Add(b);</c> is <c>var l = [a, b];</c>, which
/// Rust writes <c>vec![a, b]</c> - clippy's <c>vec_init_then_push</c> rejects the pushes. The elements
/// are evaluated in the same order; an element that names the list stops the merge.
/// </summary>
internal static class ListLiterals {

  /// <summary>The module with the lists of every function made of their first elements where they may be.</summary>
  public static IrModule Run(IrModule module) {
    if (!module.Functions.Any(function => ArrayOwnership.Statements(function.Body).Any(statement => statement is IrDeclare { Initializer: IrCollection { Op: IrCollectionOp.ListNew } })))
      return module;

    return module with { Functions = [.. module.Functions.Select(function => function with { Body = Block(function.Body) })] };
  }

  private static IrBlock Block(IrBlock block) {
    var statements = new List<IrStmt>();
    for (var index = 0; index < block.Statements.Count; ++index) {
      var statement = block.Statements[index];
      if (statement is not IrDeclare { Initializer: IrCollection { Op: IrCollectionOp.ListNew, Arguments.Count: 0 } created } declare) {
        statements.Add(Nested(statement));
        continue;
      }

      var elements = new List<IrExpr>();
      while (index + 1 < block.Statements.Count && Added(block.Statements[index + 1], declare.Local) is { } element) {
        elements.Add(element);
        ++index;
      }

      statements.Add(elements.Count == 0 ? statement : declare with { Initializer = created with { Op = IrCollectionOp.ListOf, Arguments = elements } });
    }

    return new IrBlock(statements);
  }

  /// <summary>The element a statement adds to a list variable, <c>l.Add(v)</c> where <c>v</c> does not name the list; <see langword="null"/> for any other statement.</summary>
  private static IrExpr? Added(IrStmt statement, IrLocal list)
    => statement is IrExprStmt { Expression: IrCollection { Op: IrCollectionOp.ListAdd, Arguments: [IrLocalRef target, var value] } } && target.Local == list
      && !IrTree.Nodes(value).Any(node => node is IrLocalRef reference && reference.Local == list) ? value : null;

  /// <summary>A statement with the blocks inside it rewritten.</summary>
  private static IrStmt Nested(IrStmt statement) {
    if (statement is IrBlock inner)
      return Block(inner);

    var blocks = IrTree.Blocks(statement);
    return blocks.Count == 0 ? statement : IrTree.With(statement, IrTree.Expressions(statement), [.. blocks.Select(Block)]);
  }

}
