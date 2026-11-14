using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Backends.CSharp.Syntax;

/// <summary>
/// Walks and rewrites a C# syntax tree: every node below a node, and a node with each of its
/// children replaced, the children first. A rewrite returns the node itself where nothing below it
/// changed, so a pass that changes nothing keeps the tree it was given.
/// </summary>
public static class CSharpSyntaxRewriter {

  /// <summary>The children of a node, in the order the printer writes them.</summary>
  /// <param name="node">The node.</param>
  /// <returns>The children.</returns>
  public static IEnumerable<CsNode> Children(CsNode node) => node switch {
    CsMemberAccess access => [access.Target],
    CsInvocation call => [call.Target, .. call.Arguments],
    CsOutArgument argument => [argument.Value],
    CsElementAccess element => [element.Target, element.Index],
    CsCast cast => [cast.Operand],
    CsPrefixUnary unary => [unary.Operand],
    CsPostfixUnary unary => [unary.Operand],
    CsBinary binary => [binary.Left, binary.Right],
    CsIsType test => [test.Operand],
    CsAssignment assignment => [assignment.Left, assignment.Right],
    CsConditional choice => [choice.Condition, choice.WhenTrue, choice.WhenFalse],
    CsParenthesized parenthesized => [parenthesized.Inner],
    CsCheckedExpression context => [context.Inner],
    CsObjectCreation creation => creation.Arguments,
    CsArrayCreation creation => [.. Optional(creation.Size), .. Optional(creation.Initializer)],
    CsArrayInitializer initializer => initializer.Elements,
    CsBlock block => block.Statements,
    CsLocalDeclaration declaration => Optional(declaration.Initializer),
    CsExpressionStatement statement => [statement.Expression],
    CsIf conditional => [conditional.Condition, conditional.Then, .. Optional(conditional.Else)],
    CsWhile loop => [loop.Condition, loop.Body],
    CsDo loop => [loop.Body, loop.Condition],
    CsFor loop => [.. Optional(loop.Declaration), .. Optional(loop.Condition), .. loop.Incrementors, loop.Body],
    CsSwitch choice => [choice.Value, .. choice.Sections],
    CsSwitchSection section => [.. section.Labels.OfType<CsExpression>(), .. section.Statements],
    CsReturn result => Optional(result.Value),
    CsThrow exception => [exception.Value],
    CsCheckedStatement context => [context.Block],
    CsField field => Optional(field.Initializer),
    CsMethod method => [.. Optional(method.Body), .. Optional(method.ExpressionBody)],
    CsConstructor constructor => [constructor.Body],
    CsTypeDeclaration type => type.Members ?? [],
    CsNamespace scope => scope.Members,
    CsCompilationUnit unit => unit.Members,
    _ => []
  };

  private static IEnumerable<CsNode> Optional(CsNode? node) => node is null ? [] : [node];

  /// <summary>Every node below a node and the node itself, each before its children.</summary>
  /// <param name="node">The node.</param>
  /// <returns>The nodes.</returns>
  public static IEnumerable<CsNode> DescendantsAndSelf(CsNode node) {
    var pending = new Stack<CsNode>();
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
  public static T Rewrite<T>(T node, Func<CsNode, CsNode> rewrite) where T : CsNode => (T)Visit(node, rewrite);

  private static CsNode Visit(CsNode node, Func<CsNode, CsNode> rewrite) => rewrite(node switch {
    CsMemberAccess access => Same(access, access.Target, rewrite, target => access with { Target = target }),
    CsInvocation call => Invocation(call, rewrite),
    CsOutArgument argument => Same(argument, argument.Value, rewrite, value => argument with { Value = value }),
    CsElementAccess element => Pair(element, element.Target, element.Index, rewrite, (target, index) => element with { Target = target, Index = index }),
    CsCast cast => Same(cast, cast.Operand, rewrite, operand => cast with { Operand = operand }),
    CsPrefixUnary unary => Same(unary, unary.Operand, rewrite, operand => unary with { Operand = operand }),
    CsPostfixUnary unary => Same(unary, unary.Operand, rewrite, operand => unary with { Operand = operand }),
    CsBinary binary => Pair(binary, binary.Left, binary.Right, rewrite, (left, right) => binary with { Left = left, Right = right }),
    CsIsType test => Same(test, test.Operand, rewrite, operand => test with { Operand = operand }),
    CsAssignment assignment => Pair(assignment, assignment.Left, assignment.Right, rewrite, (left, right) => assignment with { Left = left, Right = right }),
    CsConditional choice => Conditional(choice, rewrite),
    CsParenthesized parenthesized => Same(parenthesized, parenthesized.Inner, rewrite, inner => parenthesized with { Inner = inner }),
    CsCheckedExpression context => Same(context, context.Inner, rewrite, inner => context with { Inner = inner }),
    CsObjectCreation creation => List(creation.Arguments, rewrite) is var arguments && !ReferenceEquals(arguments, creation.Arguments) ? creation with { Arguments = arguments } : creation,
    CsArrayCreation creation => ArrayCreation(creation, rewrite),
    CsArrayInitializer initializer => List(initializer.Elements, rewrite) is var elements && !ReferenceEquals(elements, initializer.Elements) ? initializer with { Elements = elements } : initializer,
    CsBlock block => List(block.Statements, rewrite) is var statements && !ReferenceEquals(statements, block.Statements) ? block with { Statements = statements } : block,
    CsLocalDeclaration declaration => declaration.Initializer is null ? declaration : Same(declaration, declaration.Initializer, rewrite, value => declaration with { Initializer = value }),
    CsExpressionStatement statement => Same(statement, statement.Expression, rewrite, expression => statement with { Expression = expression }),
    CsIf conditional => If(conditional, rewrite),
    CsWhile loop => Pair(loop, loop.Condition, loop.Body, rewrite, (condition, body) => loop with { Condition = condition, Body = body }),
    CsDo loop => Pair(loop, loop.Body, loop.Condition, rewrite, (body, condition) => loop with { Body = body, Condition = condition }),
    CsFor loop => For(loop, rewrite),
    CsSwitch choice => Switch(choice, rewrite),
    CsSwitchSection section => Section(section, rewrite),
    CsReturn { Value: { } value } result => Same(result, value, rewrite, rewritten => result with { Value = rewritten }),
    CsThrow exception => Same(exception, exception.Value, rewrite, value => exception with { Value = value }),
    CsCheckedStatement context => Same(context, context.Block, rewrite, block => context with { Block = block }),
    CsField { Initializer: { } initializer } field => Same(field, initializer, rewrite, value => field with { Initializer = value }),
    CsMethod method => Method(method, rewrite),
    CsConstructor constructor => Same(constructor, constructor.Body, rewrite, body => constructor with { Body = body }),
    CsTypeDeclaration { Members: { } members } type => List(members, rewrite) is var rewritten && !ReferenceEquals(rewritten, members) ? type with { Members = rewritten } : type,
    CsNamespace scope => List(scope.Members, rewrite) is var members && !ReferenceEquals(members, scope.Members) ? scope with { Members = members } : scope,
    CsCompilationUnit unit => List(unit.Members, rewrite) is var members && !ReferenceEquals(members, unit.Members) ? unit with { Members = members } : unit,
    _ => node
  });

  private static TNode Same<TNode, TChild>(TNode node, TChild child, Func<CsNode, CsNode> rewrite, Func<TChild, TNode> rebuild) where TNode : CsNode where TChild : CsNode {
    var rewritten = (TChild)Visit(child, rewrite);
    return ReferenceEquals(rewritten, child) ? node : rebuild(rewritten);
  }

  private static TNode Pair<TNode, TFirst, TSecond>(TNode node, TFirst first, TSecond second, Func<CsNode, CsNode> rewrite, Func<TFirst, TSecond, TNode> rebuild)
    where TNode : CsNode where TFirst : CsNode where TSecond : CsNode {
    var one = (TFirst)Visit(first, rewrite);
    var two = (TSecond)Visit(second, rewrite);
    return ReferenceEquals(one, first) && ReferenceEquals(two, second) ? node : rebuild(one, two);
  }

  /// <summary>A list with every element rewritten; the list itself when no element changed.</summary>
  private static IReadOnlyList<T> List<T>(IReadOnlyList<T> list, Func<CsNode, CsNode> rewrite) where T : CsNode {
    List<T>? changed = null;
    for (var index = 0; index < list.Count; ++index) {
      var rewritten = (T)Visit(list[index], rewrite);
      if (changed is null && !ReferenceEquals(rewritten, list[index]))
        changed = [.. list.Take(index)];

      changed?.Add(rewritten);
    }

    return changed is null ? list : changed;
  }

  private static T? Optional<T>(T? node, Func<CsNode, CsNode> rewrite) where T : CsNode => node is null ? null : (T)Visit(node, rewrite);

  private static CsInvocation Invocation(CsInvocation call, Func<CsNode, CsNode> rewrite) {
    var target = (CsExpression)Visit(call.Target, rewrite);
    var arguments = List(call.Arguments, rewrite);
    return ReferenceEquals(target, call.Target) && ReferenceEquals(arguments, call.Arguments) ? call : call with { Target = target, Arguments = arguments };
  }

  private static CsConditional Conditional(CsConditional choice, Func<CsNode, CsNode> rewrite) {
    var condition = (CsExpression)Visit(choice.Condition, rewrite);
    var whenTrue = (CsExpression)Visit(choice.WhenTrue, rewrite);
    var whenFalse = (CsExpression)Visit(choice.WhenFalse, rewrite);
    return ReferenceEquals(condition, choice.Condition) && ReferenceEquals(whenTrue, choice.WhenTrue) && ReferenceEquals(whenFalse, choice.WhenFalse)
      ? choice
      : choice with { Condition = condition, WhenTrue = whenTrue, WhenFalse = whenFalse };
  }

  private static CsArrayCreation ArrayCreation(CsArrayCreation creation, Func<CsNode, CsNode> rewrite) {
    var size = Optional(creation.Size, rewrite);
    var initializer = Optional(creation.Initializer, rewrite);
    return ReferenceEquals(size, creation.Size) && ReferenceEquals(initializer, creation.Initializer) ? creation : creation with { Size = size, Initializer = initializer };
  }

  private static CsIf If(CsIf conditional, Func<CsNode, CsNode> rewrite) {
    var condition = (CsExpression)Visit(conditional.Condition, rewrite);
    var then = (CsStatement)Visit(conditional.Then, rewrite);
    var otherwise = Optional(conditional.Else, rewrite);
    return ReferenceEquals(condition, conditional.Condition) && ReferenceEquals(then, conditional.Then) && ReferenceEquals(otherwise, conditional.Else)
      ? conditional
      : conditional with { Condition = condition, Then = then, Else = otherwise };
  }

  private static CsFor For(CsFor loop, Func<CsNode, CsNode> rewrite) {
    var declaration = Optional(loop.Declaration, rewrite);
    var condition = Optional(loop.Condition, rewrite);
    var incrementors = List(loop.Incrementors, rewrite);
    var body = (CsStatement)Visit(loop.Body, rewrite);
    return ReferenceEquals(declaration, loop.Declaration) && ReferenceEquals(condition, loop.Condition) && ReferenceEquals(incrementors, loop.Incrementors) && ReferenceEquals(body, loop.Body)
      ? loop
      : loop with { Declaration = declaration, Condition = condition, Incrementors = incrementors, Body = body };
  }

  private static CsSwitch Switch(CsSwitch choice, Func<CsNode, CsNode> rewrite) {
    var value = (CsExpression)Visit(choice.Value, rewrite);
    var sections = List(choice.Sections, rewrite);
    return ReferenceEquals(value, choice.Value) && ReferenceEquals(sections, choice.Sections) ? choice : choice with { Value = value, Sections = sections };
  }

  private static CsSwitchSection Section(CsSwitchSection section, Func<CsNode, CsNode> rewrite) {
    var labels = section.Labels.Select(label => Optional(label, rewrite)).ToList();
    var statements = List(section.Statements, rewrite);
    return labels.SequenceEqual(section.Labels, ReferenceEqualityComparer.Instance) && ReferenceEquals(statements, section.Statements)
      ? section
      : section with { Labels = labels, Statements = statements };
  }

  private static CsMethod Method(CsMethod method, Func<CsNode, CsNode> rewrite) {
    var body = Optional(method.Body, rewrite);
    var expression = Optional(method.ExpressionBody, rewrite);
    return ReferenceEquals(body, method.Body) && ReferenceEquals(expression, method.ExpressionBody) ? method : method with { Body = body, ExpressionBody = expression };
  }

}
