using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize;

/// <summary>
/// Idioms written as a run of assignments to one variable - <c>x = f1(x); x = f2(x); …</c> - such as a
/// swap cascade, a SWAR count or an exclusive-or fold. The run is composed into one expression of the
/// variable's value before it, which a rule recognizes either as the final value of the variable or
/// inside the statement that follows and reads it.
/// </summary>
internal static class Windows {

  /// <summary>The longest run looked at; each step may read the variable more than once.</summary>
  private const int Longest = 8;

  /// <summary>
  /// Recognizes a run of assignments that starts at a position.
  /// <list type="bullet">
  /// <item>The run, or a prefix of it with at least two assignments, composes to a value the rule
  /// recognizes: it becomes one assignment of that value.</item>
  /// <item>The whole run is followed by a return, a declaration or an assignment of another variable
  /// whose value, with the composed value read for the variable, the rule recognizes; and nothing
  /// reads the variable afterwards: the run disappears and the statement reads the recognized value.</item>
  /// </list>
  /// Every assignment of the run has no effect and cannot trap, so that it may be evaluated once,
  /// many times or not at all.
  /// </summary>
  /// <param name="statements">The statements of the block.</param>
  /// <param name="index">The position the run starts at.</param>
  /// <param name="context">The facts of the function.</param>
  /// <param name="recognize">The rule: the replacement of an expression, or <see langword="null"/>.</param>
  /// <param name="rewrite">What replaces the run.</param>
  /// <returns><see langword="true"/> when the rule recognized the run.</returns>
  public static bool TryRun(IReadOnlyList<IrStmt> statements, int index, RuleContext context, Func<IrExpr, IrExpr?> recognize, out StatementRewrite? rewrite) {
    rewrite = null;
    if (statements[index] is not IrAssign { Local: var local })
      return false;

    var composed = new List<IrExpr>();
    IrExpr value = new IrLocalRef(local);
    for (var position = index; position < statements.Count && composed.Count < Longest; ++position) {
      if (statements[position] is not IrAssign assign || assign.Local != local || !IrShape.IsTrapFree(assign.Value))
        break;

      value = IrShape.Substitute(assign.Value, local, value);
      composed.Add(value);
    }

    for (var length = composed.Count; length >= 2; --length)
      if (recognize(composed[length - 1]) is { } whole && whole.Type == local.Type) {
        rewrite = new(length, [new IrAssign(local, whole)]);
        return true;
      }

    // A single assignment and its reader are a plain expression, which the rule meets as such.
    var after = index + composed.Count;
    if (composed.Count < 2 || after >= statements.Count || !context.IsDeadAfter(local, statements, after + 1))
      return false;

    var (reader, read) = statements[after] switch {
      IrReturn { Value: { } result } => ((Func<IrExpr, IrStmt>)(expression => new IrReturn(expression)), result),
      IrDeclare { Initializer: { } initial } declare when declare.Local != local => (expression => new IrDeclare(declare.Local, expression), initial),
      IrAssign assign when assign.Local != local => (expression => new IrAssign(assign.Local, expression), assign.Value),
      _ => (null, null)
    };
    if (reader is null || read is null || !IrShape.IsSideEffectFree(read) || !IrShape.Reads(read, local))
      return false;

    var fired = false;
    var seen = new Dictionary<IrExpr, IrExpr>(ReferenceEqualityComparer.Instance);
    IrExpr Visit(IrExpr node) {
      if (seen.TryGetValue(node, out var known))
        return known;

      var operands = IrShape.Operands(node);
      var mapped = operands.Select(Visit).ToList();
      var rebuilt = mapped.Where((operand, position) => !ReferenceEquals(operand, operands[position])).Any() ? IrShape.With(node, mapped) : node;
      var replaced = recognize(rebuilt);
      fired |= replaced is not null;
      return seen[node] = replaced ?? rebuilt;
    }

    var substituted = Visit(IrShape.Substitute(read, local, value));
    if (!fired || IrShape.Size(substituted) > IrShape.Size(read) + 4)
      return false;

    rewrite = new(composed.Count + 1, [reader(substituted)]);
    return true;
  }

}
