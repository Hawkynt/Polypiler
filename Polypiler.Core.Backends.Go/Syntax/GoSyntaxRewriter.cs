using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Polypiler.Core.Backends.Go.Syntax;

/// <summary>Walks a Go syntax tree: every node below one, and the shape two trees compare by.</summary>
public static class GoSyntaxRewriter {

  /// <summary>A node and every node below it, depth first, the node first.</summary>
  /// <param name="node">The tree.</param>
  /// <returns>The nodes.</returns>
  public static IEnumerable<GoNode> DescendantsAndSelf(GoNode node) {
    ArgumentNullException.ThrowIfNull(node);
    var pending = new Stack<GoNode>();
    pending.Push(node);
    while (pending.TryPop(out var current)) {
      yield return current;
      var children = Children(current).ToList();
      for (var index = children.Count - 1; index >= 0; --index)
        pending.Push(children[index]);
    }
  }

  /// <summary>The nodes directly below a node, in source order.</summary>
  /// <param name="node">The node.</param>
  /// <returns>The children.</returns>
  public static IEnumerable<GoNode> Children(GoNode node) {
    ArgumentNullException.ThrowIfNull(node);
    IEnumerable<GoNode?> children = node switch {
      GoBinary binary => [binary.Left, binary.Right],
      GoUnary unary => [unary.Operand],
      GoStar star => [star.Operand],
      GoCall call => [call.Callee, .. call.Arguments],
      GoSelector selector => [selector.Target],
      GoIndex index => [index.Target, index.Index],
      GoSliceExpr slice => [slice.Target, slice.Low, slice.High, slice.Max],
      GoComposite composite => [composite.LiteralType, .. composite.Elements],
      GoKeyValue pair => [pair.Key, pair.Value],
      GoFuncLit literal => [literal.Signature, literal.Body],
      GoArrayType array => [array.Length, array.Element],
      GoMapType map => [map.Key, map.Value],
      GoFuncType function => [.. function.Parameters, .. function.Results],
      GoStructType structure => structure.Fields,
      GoField field => [field.FieldType],
      GoBlock block => block.Statements,
      GoExprStmt statement => [statement.Expression],
      GoAssign assignment => [.. assignment.Left, .. assignment.Right],
      GoIncDec step => [step.Target],
      GoValueSpec spec => [spec.SpecType, .. spec.Values],
      GoVarStmt variable => [variable.Spec],
      GoReturn result => result.Results,
      GoBlockStmt block => [block.Body],
      GoIf conditional => [conditional.Init, conditional.Condition, conditional.Then, conditional.Else],
      GoFor loop => [loop.Init, loop.Condition, loop.Post, loop.Body],
      GoRange range => [range.Key, range.Value, range.Over, range.Body],
      GoSwitch choice => [choice.Init, choice.Tag, .. choice.Clauses],
      GoCaseClause clause => [.. clause.Values ?? [], .. clause.Body],
      GoLabeled labeled => [labeled.Statement],
      GoDefer deferred => [deferred.Call],
      GoFuncDecl function => [function.Receiver, function.Signature, function.Body],
      GoConstDecl constant => [constant.Spec],
      GoVarDecl variable => [variable.Spec],
      GoTypeDecl type => [type.Declared],
      GoFile file => file.Declarations,
      _ => []
    };
    return children.OfType<GoNode>();
  }

  /// <summary>Every name a tree reads, calls or selects from: identifiers, and the package of a selector.</summary>
  /// <param name="node">The tree.</param>
  /// <returns>The names.</returns>
  public static IEnumerable<string> Names(GoNode node) => DescendantsAndSelf(node).OfType<GoIdent>().Select(name => name.Name);

  /// <summary>
  /// The shape of a tree: its nodes and what they mean, as text, without the properties that only
  /// say how the tree is laid out (blank lines, comments, elements per line) and without the IR types.
  /// </summary>
  /// <param name="node">The tree.</param>
  /// <returns>The shape.</returns>
  public static string Shape(GoNode node) {
    ArgumentNullException.ThrowIfNull(node);
    var text = new StringBuilder();
    Write(node, text);
    return text.ToString();
  }

  private static void Write(GoNode? node, StringBuilder text) {
    if (node is null) {
      text.Append('_');
      return;
    }

    text.Append(node.GetType().Name[2..]);
    var detail = node switch {
      GoIdent name => name.Name,
      GoInt literal => literal.Value + (literal.Hex ? "h" : ""),
      GoFloat literal => literal.Text,
      GoString literal => literal.Value,
      GoBinary binary => binary.Op,
      GoUnary unary => unary.Op,
      GoCall call => call.Spread ? "..." : null,
      GoSelector selector => selector.Name,
      GoField field => string.Join(",", field.Names),
      GoAssign assignment => assignment.Op,
      GoIncDec step => step.Increment ? "++" : "--",
      GoValueSpec spec => string.Join(",", spec.Names),
      GoRange range => range.Define ? ":=" : "=",
      GoCaseClause clause => clause.Values is null ? "default" : null,
      GoBranch branch => branch.Keyword + " " + branch.Label,
      GoLabeled labeled => labeled.Label,
      GoFuncDecl function => function.Name,
      GoTypeDecl type => type.Name,
      GoFile file => file.Package + "[" + string.Join(",", file.Imports.Order(StringComparer.Ordinal)) + "]",
      _ => null
    };
    if (detail is not null)
      text.Append('<').Append(detail).Append('>');

    text.Append('(');
    var first = true;
    GoNode?[] children = node switch {
      GoSliceExpr slice => [slice.Target, slice.Low, slice.High, slice.Max],
      GoIf conditional => [conditional.Init, conditional.Condition, conditional.Then, conditional.Else],
      GoFor loop => [loop.Init, loop.Condition, loop.Post, loop.Body],
      GoRange range => [range.Key, range.Value, range.Over, range.Body],
      GoSwitch choice => [choice.Init, choice.Tag, .. choice.Clauses],
      GoArrayType array => [array.Length, array.Element],
      GoComposite composite => [composite.LiteralType, .. composite.Elements],
      GoValueSpec spec => [spec.SpecType, .. spec.Values],
      GoFuncDecl function => [function.Receiver, function.Signature, function.Body],
      _ => [.. Children(node)]
    };
    foreach (var child in children) {
      if (!first)
        text.Append(',');

      first = false;
      Write(child, text);
    }

    text.Append(')');
  }

}
