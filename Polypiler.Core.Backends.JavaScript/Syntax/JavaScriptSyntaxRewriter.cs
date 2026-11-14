using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Backends.JavaScript.Syntax;

/// <summary>
/// Walks and rewrites a JavaScript syntax tree: every node below a node, and a node with each of its
/// children replaced, the children first. A rewrite returns the node itself where nothing below it
/// changed, so a pass that changes nothing keeps the tree it was given.
/// </summary>
public static class JavaScriptSyntaxRewriter {

  /// <summary>The children of a node, in the order the printer writes them.</summary>
  /// <param name="node">The node.</param>
  /// <returns>The children.</returns>
  public static IEnumerable<JsNode> Children(JsNode node) => node switch {
    JsMember member => [member.Target],
    JsIndex index => [index.Target, index.Index],
    JsCall call => [call.Callee, .. call.Arguments],
    JsNew creation => [creation.Callee, .. creation.Arguments],
    JsUnary unary => [unary.Operand],
    JsUpdate update => [update.Operand],
    JsBinary binary => [binary.Left, binary.Right],
    JsAssignment assignment => [assignment.Left, assignment.Right],
    JsConditional choice => [choice.Test, choice.WhenTrue, choice.WhenFalse],
    JsSequence sequence => sequence.Expressions,
    JsParenthesized parenthesized => [parenthesized.Inner],
    JsArray array => array.Elements,
    JsObject literal => literal.Properties,
    JsProperty property => [property.Value],
    JsFunctionExpression function => [function.Body],
    JsBlock block => block.Statements,
    JsDeclarator declarator => Optional(declarator.Initializer),
    JsVariableDeclaration declaration => declaration.Declarators,
    JsExpressionStatement statement => [statement.Expression],
    JsIf conditional => [conditional.Test, conditional.Then, .. Optional(conditional.Else)],
    JsWhile loop => [loop.Test, loop.Body],
    JsDoWhile loop => [loop.Body, loop.Test],
    JsFor loop => [.. Optional(loop.Declaration), .. Optional(loop.Test), .. Optional(loop.Update), loop.Body],
    JsSwitchCase @case => [.. Optional(@case.Test), .. @case.Consequent],
    JsSwitch choice => [choice.Discriminant, .. choice.Cases],
    JsReturn result => Optional(result.Value),
    JsThrow exception => [exception.Value],
    JsFunctionDeclaration function => [function.Body],
    JsExport export => [export.Declaration],
    JsProgram program => program.Body,
    _ => []
  };

  private static IEnumerable<JsNode> Optional(JsNode? node) => node is null ? [] : [node];

  /// <summary>Every node below a node and the node itself, each before its children.</summary>
  /// <param name="node">The node.</param>
  /// <returns>The nodes.</returns>
  public static IEnumerable<JsNode> DescendantsAndSelf(JsNode node) {
    var pending = new Stack<JsNode>();
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
  public static T Rewrite<T>(T node, Func<JsNode, JsNode> rewrite) where T : JsNode => (T)Visit(node, rewrite);

  private static JsNode Visit(JsNode node, Func<JsNode, JsNode> rewrite) {
    var children = Children(node).ToList();
    if (children.Count == 0)
      return rewrite(node);

    var rewritten = children.Select(child => Visit(child, rewrite)).ToList();
    return rewrite(rewritten.SequenceEqual(children, ReferenceEqualityComparer.Instance) ? node : Rebuild(node, new Queue<JsNode>(rewritten)));
  }

  /// <summary>A node with its children replaced by the given ones, in the order of <see cref="Children"/>.</summary>
  private static JsNode Rebuild(JsNode node, Queue<JsNode> children) {
    T Next<T>() where T : JsNode => (T)children.Dequeue();
    T? Maybe<T>(T? current) where T : JsNode => current is null ? null : Next<T>();
    IReadOnlyList<T> Many<T>(int count) where T : JsNode => [.. Enumerable.Range(0, count).Select(_ => Next<T>())];
    return node switch {
      JsMember member => member with { Target = Next<JsExpression>() },
      JsIndex index => index with { Target = Next<JsExpression>(), Index = Next<JsExpression>() },
      JsCall call => call with { Callee = Next<JsExpression>(), Arguments = Many<JsExpression>(call.Arguments.Count) },
      JsNew creation => creation with { Callee = Next<JsExpression>(), Arguments = Many<JsExpression>(creation.Arguments.Count) },
      JsUnary unary => unary with { Operand = Next<JsExpression>() },
      JsUpdate update => update with { Operand = Next<JsExpression>() },
      JsBinary binary => binary with { Left = Next<JsExpression>(), Right = Next<JsExpression>() },
      JsAssignment assignment => assignment with { Left = Next<JsExpression>(), Right = Next<JsExpression>() },
      JsConditional choice => choice with { Test = Next<JsExpression>(), WhenTrue = Next<JsExpression>(), WhenFalse = Next<JsExpression>() },
      JsSequence sequence => sequence with { Expressions = Many<JsExpression>(sequence.Expressions.Count) },
      JsParenthesized parenthesized => parenthesized with { Inner = Next<JsExpression>() },
      JsArray array => array with { Elements = Many<JsExpression>(array.Elements.Count) },
      JsObject literal => literal with { Properties = Many<JsProperty>(literal.Properties.Count) },
      JsProperty property => property with { Value = Next<JsExpression>() },
      JsFunctionExpression function => function with { Body = Next<JsBlock>() },
      JsBlock block => block with { Statements = Many<JsStatement>(block.Statements.Count) },
      JsDeclarator declarator => declarator with { Initializer = Next<JsExpression>() },
      JsVariableDeclaration declaration => declaration with { Declarators = Many<JsDeclarator>(declaration.Declarators.Count) },
      JsExpressionStatement statement => statement with { Expression = Next<JsExpression>() },
      JsIf conditional => conditional with { Test = Next<JsExpression>(), Then = Next<JsStatement>(), Else = Maybe(conditional.Else) },
      JsWhile loop => loop with { Test = Next<JsExpression>(), Body = Next<JsStatement>() },
      JsDoWhile loop => loop with { Body = Next<JsStatement>(), Test = Next<JsExpression>() },
      JsFor loop => loop with { Declaration = Maybe(loop.Declaration), Test = Maybe(loop.Test), Update = Maybe(loop.Update), Body = Next<JsStatement>() },
      JsSwitchCase @case => @case with { Test = Maybe(@case.Test), Consequent = Many<JsStatement>(@case.Consequent.Count) },
      JsSwitch choice => choice with { Discriminant = Next<JsExpression>(), Cases = Many<JsSwitchCase>(choice.Cases.Count) },
      JsReturn result => result with { Value = Next<JsExpression>() },
      JsThrow exception => exception with { Value = Next<JsExpression>() },
      JsFunctionDeclaration function => function with { Body = Next<JsBlock>() },
      JsExport export => export with { Declaration = Next<JsStatement>() },
      _ => ((JsProgram)node) with { Body = Many<JsStatement>(((JsProgram)node).Body.Count) }
    };
  }

}
