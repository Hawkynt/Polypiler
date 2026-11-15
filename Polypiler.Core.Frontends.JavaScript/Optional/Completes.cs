using System.Collections.Generic;
using System.Linq;
using Acornima.Ast;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>
/// Whether a function body can end without a <c>return</c> of a value, read from the source before
/// anything is lowered: a function that returns a value elsewhere returns <c>undefined</c> there
/// (#1322). The rules are those the lowering applies to the IR: a return ends a path, an
/// <c>if</c> with both branches ending ends it, a <c>switch</c> with a default whose sections all
/// end ends it, a throw ends it and so does a try whose block and catch block end it, and a loop
/// without a condition that no <c>break</c> stands in never ends.
/// </summary>
internal static class Completes {

  /// <summary>Whether the statements can be run to their end.</summary>
  /// <param name="statements">The statements of a block.</param>
  /// <returns><see langword="true"/> when some path leaves them at their end.</returns>
  public static bool MayComplete(IEnumerable<Statement> statements) => !statements.Any(Never);

  private static bool Never(Statement statement) => statement switch {
    ReturnStatement or ThrowStatement => true,
    BlockStatement block => block.Body.Any(Never),
    // A try ends a path where its block and its catch block both end it, or its finally block does (#1323).
    TryStatement attempt => (attempt.Block.Body.Any(Never) && (attempt.Handler is null || attempt.Handler.Body.Body.Any(Never)))
      || (attempt.Finalizer is { } final && final.Body.Any(Never)),
    IfStatement { Alternate: { } otherwise } choice => Never(choice.Consequent) && Never(otherwise),
    SwitchStatement choice => choice.Cases.Any(section => section.Test is null)
      && choice.Cases.All(section => section.Consequent.Count == 0 || section.Consequent.Any(Never)),
    WhileStatement { Test: BooleanLiteral { Value: true } } loop => !Breaks(loop.Body),
    ForStatement { Test: null or BooleanLiteral { Value: true } } loop => !Breaks(loop.Body),
    _ => false
  };

  /// <summary>Whether a <c>break</c> stands anywhere inside a statement, one of an inner loop included: that errs on the side of a path that ends.</summary>
  private static bool Breaks(Node node) => node is BreakStatement || node.ChildNodes.Any(Breaks);

}
