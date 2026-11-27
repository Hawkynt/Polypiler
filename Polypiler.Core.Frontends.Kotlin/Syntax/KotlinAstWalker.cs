using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Frontends.Kotlin.Syntax;

/// <summary>Walks the Kotlin syntax tree: the children of a node, and every node below one with its parent.</summary>
public static class KotlinAstWalker {

  /// <summary>The direct children of a node, in source order.</summary>
  /// <param name="node">The node.</param>
  /// <returns>The children.</returns>
  public static IEnumerable<KNode> Children(KNode node) {
    ArgumentNullException.ThrowIfNull(node);
    return node switch {
      KTemplate template => template.Parts,
      KMember member => [member.Receiver],
      KCall call => [call.Callee, .. call.Arguments, .. Optional(call.Trailing)],
      KIndex index => [index.Receiver, index.Index],
      KPrefix prefix => [prefix.Operand],
      KPostfix postfix => [postfix.Operand],
      KBinary binary => [binary.Left, binary.Right],
      KIs test => [test.Value],
      KAs cast => [cast.Value],
      KIf conditional => [conditional.Condition, conditional.Then, .. Optional(conditional.Else)],
      KWhenEntry entry => [.. entry.Conditions, entry.Body],
      KWhenRange range => [range.Range],
      KWhen choice => [.. Optional(choice.Subject), .. choice.Entries],
      KLambda lambda => lambda.Body,
      KThrowExpression raise => [raise.Value],
      KJumpExpression jump => [jump.Jump],
      KBlock block => block.Statements,
      KVariable variable => Optional(variable.Initializer),
      KAssignment assignment => [assignment.Target, assignment.Value],
      KExpressionStatement statement => [statement.Expression],
      KWhile loop => [loop.Condition, loop.Body],
      KDoWhile loop => [loop.Body, loop.Condition],
      KFor loop => [loop.Range, loop.Body],
      KReturn result => Optional(result.Value),
      KLocalFunction local => [local.Function],
      KFunction function => [.. function.Parameters.Select(parameter => parameter.Default).OfType<KNode>(), .. Optional(function.Body), .. Optional(function.Expression)],
      KProperty property => Optional(property.Initializer),
      KFile file => file.Declarations,
      _ => []
    };
  }

  private static IEnumerable<KNode> Optional(KNode? node) => node is null ? [] : [node];

  /// <summary>Every node below a node, each with its parent, in pre-order.</summary>
  /// <param name="node">The node.</param>
  /// <returns>The nodes and their parents.</returns>
  public static IEnumerable<(KNode Node, KNode Parent)> Descendants(KNode node) {
    ArgumentNullException.ThrowIfNull(node);
    var pending = new Stack<KNode>();
    pending.Push(node);
    while (pending.Count > 0) {
      var parent = pending.Pop();
      foreach (var child in Children(parent).Reverse()) {
        pending.Push(child);
      }

      foreach (var child in Children(parent))
        yield return (child, parent);
    }
  }

}
