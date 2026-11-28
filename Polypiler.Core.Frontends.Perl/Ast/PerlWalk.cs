using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Frontends.Perl.Ast;

/// <summary>The children of the nodes of the tree, for walks that look for a kind of node anywhere below one.</summary>
internal static class PerlWalk {

  /// <summary>The node and every node below it, depth first, without entering the bodies of subs when <paramref name="intoSubs"/> is false.</summary>
  public static IEnumerable<PerlNode> Descendants(PerlNode node, bool intoSubs = true) {
    var stack = new Stack<PerlNode>();
    stack.Push(node);
    while (stack.Count > 0) {
      var current = stack.Pop();
      yield return current;
      if (!intoSubs && current is PerlAnonSub or PerlSubDeclaration && !ReferenceEquals(current, node))
        continue;
      var children = Children(current).ToList();
      for (var i = children.Count - 1; i >= 0; --i)
        stack.Push(children[i]);
    }
  }

  /// <summary>The direct children of a node, in source order.</summary>
  public static IEnumerable<PerlNode> Children(PerlNode node) {
    switch (node) {
      case PerlInterpolation n:
        return n.Parts;
      case PerlElement n:
        return [n.Aggregate, n.Index];
      case PerlSlice n:
        return [n.Aggregate, n.Indexes];
      case PerlDeref n:
        return [n.Reference];
      case PerlLastIndex n:
        return [n.Array];
      case PerlDeclaration n:
        return n.Variables;
      case PerlLocal n:
        return [n.Target];
      case PerlUnary n:
        return n.Operand is null ? [] : [n.Operand];
      case PerlStep n:
        return [n.Target];
      case PerlBinary n:
        return [n.Left, n.Right];
      case PerlComparisonChain n:
        return n.Operands;
      case PerlAssign n:
        return [n.Target, n.Value];
      case PerlTernary n:
        return [n.Condition, n.Then, n.Else];
      case PerlList n:
        return n.Items;
      case PerlAnonArray n:
        return n.Items;
      case PerlAnonHash n:
        return n.Items;
      case PerlAnonSub n:
        return [n.Sub];
      case PerlCall n:
        return Concat(n.Handle, n.Block, n.Arguments);
      case PerlCallReference n:
        return Concat(n.Code, null, n.Arguments);
      case PerlMethodCall n:
        return Concat(n.Invocant, n.DynamicName, n.Arguments);
      case PerlPattern n:
        return new PerlNode?[] { n.Target, n.Pattern, n.Replacement }.Where(c => c is not null)!;
      case PerlCommand n:
        return [n.Command];
      case PerlDoBlock n:
        return [n.Body];
      case PerlEvalBlock n:
        return [n.Body];
      case PerlJump n:
        return n.Value is null ? [] : [n.Value];
      case PerlExpressionStatement n:
        return n.ModifierExpression is null ? [n.Expression] : [n.Expression, n.ModifierExpression];
      case PerlBlock n:
        return n.Statements;
      case PerlIf n:
        return n.Clauses.SelectMany(c => new PerlNode[] { c.Condition, c.Body }).Concat(n.Else is null ? [] : [n.Else]);
      case PerlWhile n:
        return new PerlNode?[] { n.Condition, n.Body, n.Continue }.Where(c => c is not null)!;
      case PerlCFor n:
        return new PerlNode?[] { n.Init, n.Condition, n.Step, n.Body }.Where(c => c is not null)!;
      case PerlForeach n:
        return n.Variables.Cast<PerlNode>().Append(n.List).Append(n.Body).Concat(n.Continue is null ? [] : [n.Continue]);
      case PerlBareBlock n:
        return n.Continue is null ? [n.Body] : [n.Body, n.Continue];
      case PerlSubDeclaration n:
        return [n.Sub];
      case PerlPackage n:
        return n.Body is null ? [] : [n.Body];
      case PerlUse n:
        return n.Imports is null ? [] : [n.Imports];
      case PerlPhaseBlock n:
        return [n.Body];
      case PerlTry n:
        return new PerlNode?[] { n.Body, n.CatchVariable, n.Catch, n.Finally }.Where(c => c is not null)!;
      case PerlDefer n:
        return [n.Body];
      case PerlSub n:
        return (n.Signature ?? []).Cast<PerlNode>().Concat(n.Body is null ? [] : [n.Body]);
      case PerlParameter n:
        return new PerlNode?[] { n.Variable, n.Default }.Where(c => c is not null)!;
      case PerlDocument n:
        return n.Statements;
      default:
        return [];
    }
  }

  private static IEnumerable<PerlNode> Concat(PerlNode? first, PerlNode? second, IEnumerable<PerlNode>? rest) {
    if (first is not null)
      yield return first;
    if (second is not null)
      yield return second;
    foreach (var item in rest ?? [])
      yield return item;
  }

}
