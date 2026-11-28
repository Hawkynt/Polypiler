using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Backends.Kotlin.Syntax;

/// <summary>Walks the Kotlin syntax tree: the children of a node, and every node below one.</summary>
public static class KotlinSyntaxWalker {

  /// <summary>The direct children of a node, in the order the printer writes them.</summary>
  /// <param name="node">The node.</param>
  /// <returns>The children.</returns>
  public static IEnumerable<KtNode> Children(KtNode node) {
    ArgumentNullException.ThrowIfNull(node);
    return node switch {
      KtNamedType named => named.Arguments,
      KtFunctionType function => [.. function.Parameters, function.Result],
      KtMember member => [member.Receiver],
      KtCall call => [call.Callee, .. call.TypeArguments, .. call.Arguments, .. Optional(call.Trailing)],
      KtIndex index => [index.Receiver, index.Index],
      KtPrefix prefix => [prefix.Operand],
      KtPostfix postfix => [postfix.Operand],
      KtBinary binary => [binary.Left, binary.Right],
      KtIs test => [test.Value, test.Type],
      KtAs cast => [cast.Value, cast.Type],
      KtIfExpression conditional => [conditional.Condition, conditional.Then, conditional.Else],
      KtThrowExpression raise => [raise.Value],
      KtLambda lambda => [.. lambda.Parameters, .. lambda.Body],
      KtBlock block => block.Statements,
      KtVariable variable => [.. Optional(variable.Type), .. Optional(variable.Initializer)],
      KtAssignment assignment => [assignment.Target, assignment.Value],
      KtExpressionStatement statement => [statement.Expression],
      KtIf conditional => [conditional.Condition, conditional.Then, .. Optional(conditional.Else)],
      KtWhile loop => [loop.Condition, loop.Body],
      KtDoWhile loop => [loop.Body, loop.Condition],
      KtFor loop => [loop.Range, loop.Body],
      KtWhenEntry entry => [.. entry.Conditions, entry.Body],
      KtWhen choice => [.. Optional(choice.Subject), .. choice.Entries],
      KtReturn result => Optional(result.Value),
      KtThrow raise => [raise.Value],
      KtCatch clause => [clause.Type, clause.Body],
      KtTry attempt => [attempt.Body, .. attempt.Catches, .. Optional(attempt.Finally)],
      KtLocalFunction local => [local.Function],
      KtParameter parameter => [.. Optional(parameter.Type), .. Optional(parameter.Default)],
      KtFunction function => [.. function.Parameters, .. Optional(function.Result), .. Optional(function.Body), .. Optional(function.Expression)],
      KtProperty property => [.. Optional(property.Type), .. Optional(property.Initializer)],
      KtSuperType super => [super.Type, .. super.Arguments ?? []],
      KtEnumEntry entry => entry.Arguments,
      KtConstructor constructor => [.. constructor.Parameters, .. constructor.Arguments, constructor.Body],
      KtInitializer initializer => [initializer.Body],
      KtClass type => [.. type.Parameters ?? [], .. type.SuperTypes, .. type.Entries, .. type.Members],
      KtFile file => file.Declarations,
      _ => []
    };
  }

  private static IEnumerable<KtNode> Optional(KtNode? node) => node is null ? [] : [node];

  /// <summary>The node and every node below it, in pre-order.</summary>
  /// <param name="node">The node.</param>
  /// <returns>The nodes.</returns>
  public static IEnumerable<KtNode> DescendantsAndSelf(KtNode node) {
    ArgumentNullException.ThrowIfNull(node);
    var pending = new Stack<KtNode>();
    pending.Push(node);
    while (pending.Count > 0) {
      var current = pending.Pop();
      yield return current;
      foreach (var child in Children(current).Reverse())
        pending.Push(child);
    }
  }

}
