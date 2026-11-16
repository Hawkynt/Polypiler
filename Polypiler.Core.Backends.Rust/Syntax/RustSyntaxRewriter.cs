using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Backends.Rust.Syntax;

/// <summary>
/// Walks and rewrites a Rust syntax tree: every node below a node, and a node with each of its
/// children replaced, the children first. A rewrite returns the node itself where nothing below it
/// changed, so a pass that changes nothing keeps the tree it was given.
/// </summary>
public static class RustSyntaxRewriter {

  /// <summary>The children of a node, in the order the printer writes them.</summary>
  /// <param name="node">The node.</param>
  /// <returns>The children.</returns>
  public static IEnumerable<RsNode> Children(RsNode node) => node switch {
    RsUnary unary => [unary.Operand],
    RsBinary binary => [binary.Left, binary.Right],
    RsCast cast => [cast.Operand],
    RsMethodCall call => [call.Receiver, .. call.Arguments],
    RsField field => [field.Target],
    RsCall call => [call.Callee, .. call.Arguments],
    RsIndex index => [index.Target, index.Index],
    RsParenthesized parenthesized => [parenthesized.Inner],
    RsMacro macro => macro.Arguments,
    RsAssign assign => [assign.Target, assign.Value],
    RsBlockExpression block => [block.Block],
    RsUnsafe block => [block.Block],
    RsIf choice => [choice.Condition, choice.Then, .. Optional(choice.Else)],
    RsWhile loop => [loop.Condition, loop.Body],
    RsLoop loop => [loop.Body],
    RsFor loop => [loop.Iterator, loop.Body],
    RsRange range => [.. Optional(range.Start), .. Optional(range.End)],
    RsReference reference => [reference.Operand],
    RsLabeledBlock labeled => [labeled.Body],
    RsMatchArm arm => [arm.Body],
    RsMatch choice => [choice.Scrutinee, .. choice.Arms],
    RsBreak exit => Optional(exit.Value),
    RsReturn result => Optional(result.Value),
    RsArray array => [.. array.Elements, .. Optional(array.Count)],
    RsStructLiteral literal => literal.Fields,
    RsFieldValue field => [field.Value],
    RsImpl implementation => implementation.Items,
    RsLet binding => Optional(binding.Initializer),
    RsExpressionStatement statement => [statement.Expression],
    RsBlock block => [.. block.Statements, .. Optional(block.Tail)],
    RsFunction function => [function.Body],
    RsConst constant => [constant.Value],
    RsInlineModule module => module.Items,
    RsFile file => file.Items,
    RsClosure closure => [closure.Body],
    RsTuple tuple => tuple.Elements,
    RsTry attempt => [attempt.Operand],
    RsLetCondition condition => [condition.Value],
    RsItemStatement statement => [statement.Item],
    _ => []
  };

  private static IEnumerable<RsNode> Optional(RsNode? node) => node is null ? [] : [node];

  /// <summary>Every node below a node and the node itself, each before its children.</summary>
  /// <param name="node">The node.</param>
  /// <returns>The nodes.</returns>
  public static IEnumerable<RsNode> DescendantsAndSelf(RsNode node) {
    var pending = new Stack<RsNode>();
    pending.Push(node);
    while (pending.Count > 0) {
      var current = pending.Pop();
      yield return current;
      foreach (var child in Children(current).Reverse())
        pending.Push(child);
    }
  }

  /// <summary>
  /// A node with every node below it rewritten, children before their parents: <paramref name="rewrite"/>
  /// sees each node with its children already rewritten and returns it or its replacement.
  /// </summary>
  /// <typeparam name="T">The kind of the node.</typeparam>
  /// <param name="node">The node.</param>
  /// <param name="rewrite">The rewrite of one node.</param>
  /// <returns>The rewritten node, which must be of the same kind.</returns>
  public static T Rewrite<T>(T node, Func<RsNode, RsNode> rewrite) where T : RsNode => (T)Visit(node, rewrite);

  private static RsNode Visit(RsNode node, Func<RsNode, RsNode> rewrite) {
    var children = Children(node).ToList();
    if (children.Count == 0)
      return rewrite(node);

    var rewritten = children.Select(child => Visit(child, rewrite)).ToList();
    return rewrite(rewritten.SequenceEqual(children, ReferenceEqualityComparer.Instance) ? node : Rebuild(node, new Queue<RsNode>(rewritten)));
  }

  /// <summary>A node with its children replaced by the given ones, in the order of <see cref="Children"/>.</summary>
  private static RsNode Rebuild(RsNode node, Queue<RsNode> children) {
    T Next<T>() where T : RsNode => (T)children.Dequeue();
    T? Maybe<T>(T? current) where T : RsNode => current is null ? null : Next<T>();
    IReadOnlyList<T> Many<T>(int count) where T : RsNode => [.. Enumerable.Range(0, count).Select(_ => Next<T>())];
    return node switch {
      RsUnary unary => unary with { Operand = Next<RsExpression>() },
      RsBinary binary => binary with { Left = Next<RsExpression>(), Right = Next<RsExpression>() },
      RsCast cast => cast with { Operand = Next<RsExpression>() },
      RsMethodCall call => call with { Receiver = Next<RsExpression>(), Arguments = Many<RsExpression>(call.Arguments.Count) },
      RsField field => field with { Target = Next<RsExpression>() },
      RsCall call => call with { Callee = Next<RsExpression>(), Arguments = Many<RsExpression>(call.Arguments.Count) },
      RsIndex index => index with { Target = Next<RsExpression>(), Index = Next<RsExpression>() },
      RsParenthesized parenthesized => parenthesized with { Inner = Next<RsExpression>() },
      RsMacro macro => macro with { Arguments = Many<RsExpression>(macro.Arguments.Count) },
      RsAssign assign => assign with { Target = Next<RsExpression>(), Value = Next<RsExpression>() },
      RsBlockExpression block => block with { Block = Next<RsBlock>() },
      RsUnsafe block => block with { Block = Next<RsBlock>() },
      RsIf choice => choice with { Condition = Next<RsExpression>(), Then = Next<RsBlock>(), Else = Maybe(choice.Else) },
      RsWhile loop => loop with { Condition = Next<RsExpression>(), Body = Next<RsBlock>() },
      RsLoop loop => loop with { Body = Next<RsBlock>() },
      RsFor loop => loop with { Iterator = Next<RsExpression>(), Body = Next<RsBlock>() },
      RsRange range => range with { Start = Maybe(range.Start), End = Maybe(range.End) },
      RsReference reference => reference with { Operand = Next<RsExpression>() },
      RsLabeledBlock labeled => labeled with { Body = Next<RsBlock>() },
      RsMatchArm arm => arm with { Body = Next<RsExpression>() },
      RsMatch choice => choice with { Scrutinee = Next<RsExpression>(), Arms = Many<RsMatchArm>(choice.Arms.Count) },
      RsBreak exit => exit with { Value = Next<RsExpression>() },
      RsReturn result => result with { Value = Next<RsExpression>() },
      RsArray array => array with { Elements = Many<RsExpression>(array.Elements.Count), Count = Maybe(array.Count) },
      RsStructLiteral literal => literal with { Fields = Many<RsFieldValue>(literal.Fields.Count) },
      RsFieldValue field => field with { Value = Next<RsExpression>() },
      RsImpl implementation => implementation with { Items = Many<RsItem>(implementation.Items.Count) },
      RsLet binding => binding with { Initializer = Next<RsExpression>() },
      RsExpressionStatement statement => statement with { Expression = Next<RsExpression>() },
      RsBlock block => block with { Statements = Many<RsStatement>(block.Statements.Count), Tail = Maybe(block.Tail) },
      RsFunction function => function with { Body = Next<RsBlock>() },
      RsConst constant => constant with { Value = Next<RsExpression>() },
      RsInlineModule module => module with { Items = Many<RsItem>(module.Items.Count) },
      RsClosure closure => closure with { Body = Next<RsExpression>() },
      RsTuple tuple => tuple with { Elements = Many<RsExpression>(tuple.Elements.Count) },
      RsTry attempt => attempt with { Operand = Next<RsExpression>() },
      RsLetCondition condition => condition with { Value = Next<RsExpression>() },
      RsItemStatement statement => statement with { Item = Next<RsItem>() },
      _ => ((RsFile)node) with { Items = Many<RsItem>(((RsFile)node).Items.Count) }
    };
  }

}
