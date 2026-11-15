using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Polypiler.Core.Backends.Python.Syntax;

/// <summary>
/// Walks and rewrites a Python syntax tree: <see cref="DescendantsAndSelf"/> lists every node,
/// <see cref="Rewrite{T}"/> replaces nodes bottom-up, and <see cref="Shape"/> writes the structure of
/// a tree without its layout and types, which is what two trees are compared by.
/// </summary>
public static class PythonSyntaxRewriter {

  /// <summary>The children of a node, in source order.</summary>
  /// <param name="node">The node.</param>
  /// <returns>Its children.</returns>
  public static IEnumerable<PyNode> Children(PyNode node) => node switch {
    PyAttribute attribute => [attribute.Target],
    PySubscript subscript => [subscript.Target, subscript.Index],
    PyCall call => [call.Callee, .. call.Arguments, .. call.Keywords],
    PyKeyword keyword => [keyword.Value],
    PyStarred starred => [starred.Value],
    PySlice slice => [.. Optional(slice.Lower), .. Optional(slice.Upper)],
    PyDict dict => dict.Keys.Zip(dict.Values).SelectMany(pair => new PyNode[] { pair.First, pair.Second }),
    PyLambda lambda => [lambda.Body],
    PyYield yielded => Optional(yielded.Value),
    PyAwait awaited => [awaited.Value],
    PyUnary unary => [unary.Operand],
    PyBinary binary => [binary.Left, binary.Right],
    PyCompare compare => compare.Operands,
    PyBoolOp boolOp => [boolOp.Left, boolOp.Right],
    PyConditional conditional => [conditional.Test, conditional.WhenTrue, conditional.WhenFalse],
    PyNamedExpression named => [named.Value],
    PyTuple tuple => tuple.Elements,
    PyList list => list.Elements,
    PyExpressionStatement statement => [statement.Expression],
    PyAssign assign => assign.Annotation is { } annotation ? [assign.Target, annotation, assign.Value] : [assign.Target, assign.Value],
    PyAnnotation annotation => [annotation.Annotation],
    PyReturn { Value: { } value } => [value],
    PyRaise raise => Optional(raise.Exception),
    PyTry attempt => [.. attempt.Body, .. attempt.Handlers, .. attempt.Finally],
    PyExceptHandler handler => [.. Optional(handler.Type), .. handler.Body],
    PyClassDef type => [.. type.Decorators, .. type.Bases, .. type.Body],
    PyIf conditional => [conditional.Test, .. conditional.Body, .. conditional.Else],
    PyWhile loop => [loop.Test, .. loop.Body],
    PyFor loop => [loop.Iterable, .. loop.Body],
    PyMatchStatement match => [match.Subject, .. match.Cases],
    PyMatchCase @case => [.. @case.Patterns, .. @case.Guard is null ? Array.Empty<PyNode>() : [@case.Guard], .. @case.Body],
    PyParameter parameter => [.. Optional(parameter.Annotation), .. Optional(parameter.Default)],
    PyFunctionDef function => [.. function.Decorators, .. function.Parameters, function.Returns, .. function.Body],
    PyModule module => module.Body,
    _ => []
  };

  private static PyNode[] Optional(PyNode? node) => node is null ? [] : [node];

  /// <summary>The node and every node below it, depth first, in source order.</summary>
  /// <param name="node">The root.</param>
  /// <returns>The nodes.</returns>
  public static IEnumerable<PyNode> DescendantsAndSelf(PyNode node) {
    var stack = new Stack<PyNode>();
    stack.Push(node);
    while (stack.Count > 0) {
      var current = stack.Pop();
      yield return current;
      foreach (var child in Children(current).Reverse())
        stack.Push(child);
    }
  }

  /// <summary>
  /// The tree with every node replaced by what <paramref name="rewrite"/> makes of it, children first.
  /// A rewrite that changes nothing returns the same tree.
  /// </summary>
  /// <typeparam name="T">The type of the root.</typeparam>
  /// <param name="node">The root.</param>
  /// <param name="rewrite">What a node becomes once its children are rewritten; the node itself to keep it.</param>
  /// <returns>The rewritten tree.</returns>
  /// <exception cref="InvalidCastException">The rewrite replaces a node by one that cannot stand in its place.</exception>
  public static T Rewrite<T>(T node, Func<PyNode, PyNode> rewrite) where T : PyNode => (T)Walk(node, rewrite);

  private static PyNode Walk(PyNode node, Func<PyNode, PyNode> rewrite) {
    PyExpression E(PyExpression expression) => (PyExpression)Walk(expression, rewrite);
    PyExpression? O(PyExpression? expression) => expression is null ? null : E(expression);
    IReadOnlyList<TItem> L<TItem>(IReadOnlyList<TItem> items) where TItem : PyNode {
      var walked = items.Select(item => (TItem)Walk(item, rewrite)).ToList();
      return walked.SequenceEqual(items, ReferenceEqualityComparer.Instance) ? items : walked;
    }

    var rebuilt = node switch {
      PyAttribute attribute => Same(attribute, attribute with { Target = E(attribute.Target) }),
      PySubscript subscript => Same(subscript, subscript with { Target = E(subscript.Target), Index = E(subscript.Index) }),
      PyCall call => Same(call, call with { Callee = E(call.Callee), Arguments = L(call.Arguments), Keywords = L(call.Keywords) }),
      PyKeyword keyword => Same(keyword, keyword with { Value = E(keyword.Value) }),
      PyStarred starred => Same(starred, starred with { Value = E(starred.Value) }),
      PySlice slice => Same(slice, slice with { Lower = O(slice.Lower), Upper = O(slice.Upper) }),
      PyDict dict => Same(dict, dict with { Keys = L(dict.Keys), Values = L(dict.Values) }),
      PyLambda lambda => Same(lambda, lambda with { Body = E(lambda.Body) }),
      PyYield yielded => Same(yielded, yielded with { Value = O(yielded.Value) }),
      PyAwait awaited => Same(awaited, awaited with { Value = E(awaited.Value) }),
      PyUnary unary => Same(unary, unary with { Operand = E(unary.Operand) }),
      PyBinary binary => Same(binary, binary with { Left = E(binary.Left), Right = E(binary.Right) }),
      PyCompare compare => Same(compare, compare with { Operands = L(compare.Operands) }),
      PyBoolOp boolOp => Same(boolOp, boolOp with { Left = E(boolOp.Left), Right = E(boolOp.Right) }),
      PyConditional conditional => Same(conditional, conditional with { Test = E(conditional.Test), WhenTrue = E(conditional.WhenTrue), WhenFalse = E(conditional.WhenFalse) }),
      PyNamedExpression named => Same(named, named with { Value = E(named.Value) }),
      PyTuple tuple => Same(tuple, tuple with { Elements = L(tuple.Elements) }),
      PyList list => Same(list, list with { Elements = L(list.Elements) }),
      PyExpressionStatement statement => Same(statement, statement with { Expression = E(statement.Expression) }),
      PyAssign assign => Same(assign, assign with { Target = E(assign.Target), Value = E(assign.Value), Annotation = assign.Annotation is null ? null : E(assign.Annotation) }),
      PyAnnotation annotation => Same(annotation, annotation with { Annotation = E(annotation.Annotation) }),
      PyReturn { Value: { } value } @return => Same(@return, @return with { Value = E(value) }),
      PyRaise raise => Same(raise, raise with { Exception = O(raise.Exception) }),
      PyTry attempt => Same(attempt, attempt with { Body = L(attempt.Body), Handlers = L(attempt.Handlers), Finally = L(attempt.Finally) }),
      PyExceptHandler handler => Same(handler, handler with { Type = O(handler.Type), Body = L(handler.Body) }),
      PyClassDef type => Same(type, type with { Decorators = L(type.Decorators), Bases = L(type.Bases), Body = L(type.Body) }),
      PyIf conditional => Same(conditional, conditional with { Test = E(conditional.Test), Body = L(conditional.Body), Else = L(conditional.Else) }),
      PyWhile loop => Same(loop, loop with { Test = E(loop.Test), Body = L(loop.Body) }),
      PyFor loop => Same(loop, loop with { Iterable = E(loop.Iterable), Body = L(loop.Body) }),
      PyMatchStatement match => Same(match, match with { Subject = E(match.Subject), Cases = L(match.Cases) }),
      PyMatchCase @case => Same(@case, @case with { Patterns = L(@case.Patterns), Guard = @case.Guard is null ? null : E(@case.Guard), Body = L(@case.Body) }),
      PyParameter parameter => Same(parameter, parameter with { Annotation = O(parameter.Annotation), Default = O(parameter.Default) }),
      PyFunctionDef function => Same(function, function with { Decorators = L(function.Decorators), Parameters = L(function.Parameters), Returns = E(function.Returns), Body = L(function.Body) }),
      PyModule module => Same(module, module with { Body = L(module.Body) }),
      _ => node
    };
    return rewrite(rebuilt);
  }

  /// <summary>The original node when the rebuilt one has the very same children, so that an unchanged tree stays the same object.</summary>
  private static PyNode Same(PyNode original, PyNode rebuilt)
    => Children(original).SequenceEqual(Children(rebuilt), ReferenceEqualityComparer.Instance) ? original : rebuilt;

  /// <summary>
  /// The structure of a tree as text: every node by its kind and the values that make its meaning,
  /// without the IR types, the layout (blank lines, comments, spellings, tables) and the helper marks.
  /// Two trees with the same shape are the same program.
  /// </summary>
  /// <param name="node">The root.</param>
  /// <returns>The shape.</returns>
  public static string Shape(PyNode node) {
    var text = new StringBuilder();
    Write(node, text);
    return text.ToString();
  }

  private static void Write(PyNode node, StringBuilder text) {
    text.Append(node.GetType().Name[2..]);
    var value = node switch {
      PyInt integer => integer.Value.ToString(CultureInfo.InvariantCulture),
      PyFloat number => BitConverter.DoubleToInt64Bits(number.Value).ToString("x16", CultureInfo.InvariantCulture),
      PyBool truth => truth.Value ? "True" : "False",
      PyString text1 => Quote(text1.Value),
      PyName name => name.Name,
      PyAttribute attribute => attribute.Name,
      PyUnary unary => unary.Operator,
      PyBinary binary => binary.Operator,
      PyCompare compare => string.Join(" ", compare.Operators),
      PyBoolOp boolOp => boolOp.Operator,
      PyNamedExpression named => named.Name,
      PyAnnotation annotation => annotation.Name,
      PyGlobal global => string.Join(",", global.Names),
      PyImport import => import.Module,
      PyImportFrom import => import.Module + ":" + string.Join(",", import.Names),
      PyFor loop => loop.Variable,
      PyParameter parameter => parameter.Name + (parameter.Annotation is null ? "" : ":") + (parameter.Default is null ? "" : "="),
      PyFunctionDef function => (function.IsAsync ? "async " : "") + function.Name,
      PyClassDef type => type.Name,
      PyKeyword keyword => keyword.Name,
      PySlice slice => (slice.Lower is null ? "-" : "+") + (slice.Upper is null ? "-" : "+"),
      PyLambda lambda => string.Join(",", lambda.Parameters),
      PyYield yielded => (yielded.From ? "from" : "") + (yielded.Value is null ? "-" : "+"),
      PyNonlocal nonlocal => string.Join(",", nonlocal.Names),
      PyExceptHandler handler => (handler.Type is null ? "-" : "+") + (handler.Name ?? ""),
      PyTry attempt => $"{attempt.Body.Count}/{attempt.Handlers.Count}/{attempt.Finally.Count}",
      PyIf conditional => $"{conditional.Body.Count}/{conditional.Else.Count}",
      PyMatchCase @case => $"{@case.Patterns.Count}/{(@case.Guard is null ? 0 : 1)}",
      PyReturn @return => @return.Value is null ? "-" : "+",
      PyAssign assign => assign.Annotation is null ? "-" : "+",
      _ => null
    };
    if (value is not null)
      text.Append(' ').Append(value);

    var children = Children(node).ToList();
    if (children.Count == 0)
      return;

    text.Append('(');
    for (var index = 0; index < children.Count; ++index) {
      if (index > 0)
        text.Append(' ');

      Write(children[index], text);
    }

    text.Append(')');
  }

  private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

}
