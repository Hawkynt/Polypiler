using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

public static partial class CSharpEmitter {

  /// <summary>
  /// A single-pass sequence over the one cursor of an iterator: every enumeration of it continues that
  /// cursor, and the first one that ends - also by <c>break</c> or an exception - disposes it, after
  /// which <c>MoveNext</c> is <see langword="false"/> and every later enumeration yields nothing. That is
  /// a generator object of ECMA-262 (§27.5): one cursor, closed by the <c>for…of</c> that leaves it.
  /// </summary>
  internal static CSharpHelper SinglePass { get; } = new("SinglePass", CollectionsNamespace + "IEnumerable<T>", CollectionsNamespace + "IEnumerator<T> cursor", [
    "try {",
    "  while (cursor.MoveNext()) {",
    "    yield return cursor.Current;",
    "  }",
    "} finally {",
    "  cursor.Dispose();",
    "}"
  ], "<T>");

  private sealed partial class Writer {

    /// <summary>Whether the function being written is an iterator, whose return without a value is <c>yield break</c>.</summary>
    private bool _iterator;

    /// <summary>The call the single-pass sequence of an iterator is being wrapped around, which is written as the call itself.</summary>
    private IrExpr? _wrapped;

    /// <summary>The C# type of a sequence: <c>IEnumerable&lt;T&gt;</c>, restartable or not.</summary>
    private string SequenceTypeName(IrType type) => $"{CollectionsNamespace}IEnumerable<{this.TypeName(type.Element!)}>";

    /// <summary>
    /// The expressions of sequences: an array as a sequence, cast to its <c>IEnumerable&lt;T&gt;</c>; the
    /// call of a single-pass iterator, whose result the helper <c>SinglePass</c> makes one cursor of.
    /// <see langword="null"/> for any other expression.
    /// </summary>
    private (CsExpression Node, int Binding)? SequenceNode(IrExpr expression) {
      switch (expression) {
        case IrArraySequence sequence:
          return (new CsCast(new CsType(this.TypeName(sequence.Type)), this.Operand(sequence.Array, Cast)) { Type = sequence.Type }, Cast);
        case IrCall or IrMethodCall when expression.Type is { IsLazySequence: true, Restartable: false } && !ReferenceEquals(expression, this._wrapped):
          this._wrapped = expression;
          var cursor = new CsInvocation(new CsMemberAccess(this.Operand(expression, Primary), "GetEnumerator"), []);
          this._wrapped = null;
          return (new CsInvocation(CSharpFragments.Name(this.Helper(SinglePass)), [cursor]) { Type = expression.Type }, Primary);
        default:
          return null;
      }
    }

    /// <summary>The C# of a sequence that a loop walks: an array as it is, any other sequence as its expression.</summary>
    private CsExpression Walked(IrExpr sequence) => sequence is IrArraySequence array ? this.Text(array.Array) : this.Text(sequence);

    /// <summary>The statements of lazy sequences; <see langword="false"/> for any other.</summary>
    private bool SequenceStatement(IrStmt statement, List<CsStatement> statements) {
      switch (statement) {
        case IrYield produced:
          statements.Add(new CsYield(this.Stored(produced.Value)));
          return true;
        case IrYieldBreak:
          statements.Add(new CsYield(null));
          return true;
        case IrYieldFrom delegated:
          // foreach (T v in s) yield return v;
          var type = new CsType(this.TypeName(delegated.Sequence.Type.Element!));
          var sequence = this.Walked(delegated.Sequence);
          var value = this.Fresh("value");
          statements.Add(new CsForEach(type, value, sequence, new CsBlock([new CsYield(new CsName(value) { Type = delegated.Sequence.Type.Element })])));
          return true;
        case IrForEachSequence loop:
          this.ForEachSequence(loop, statements);
          return true;
        default:
          return false;
      }
    }

    /// <summary>
    /// A loop over a sequence: C#'s <c>foreach</c>, which disposes the enumerator however the loop is
    /// left. Below C# 5 every pass shares the iteration variable, so where a closure captures the
    /// element the pass copies it into a variable of its own.
    /// </summary>
    private void ForEachSequence(IrForEachSequence loop, List<CsStatement> statements) {
      var sequence = this.Walked(loop.Sequence);
      var scope = new Scope();
      this._scopes.Add(scope);
      var type = new CsType(this.TypeName(loop.Element.Type));
      var captured = IrTree.Nodes(loop.Body).OfType<IrLambda>().Any(lambda => lambda.Captures.Contains(loop.Element));
      if (captured && !this.Has(CSharpFeature.ForEachIterationVariable)) {
        var shared = this.Fresh("pass");
        var name = this.Declare(loop.Element);
        var body = this.Body(loop.Body, null);
        statements.Add(new CsForEach(type, shared, sequence, new CsBlock([new CsLocalDeclaration(type, name, new CsName(shared)), .. body])));
      } else {
        var name = this.Declare(loop.Element);
        statements.Add(new CsForEach(type, name, sequence, new CsBlock(this.Body(loop.Body, null))));
      }

      this._scopes.RemoveAt(this._scopes.Count - 1);
      this._scopes[^1].Within.UnionWith(scope.Within);
    }

    /// <summary>An iterator whose body has no yield at all still is one: it ends with <c>yield break</c>.</summary>
    private void Ended(List<CsStatement> body) {
      if (this._iterator && !body.SelectMany(CSharpSyntaxRewriter.DescendantsAndSelf).OfType<CsYield>().Any())
        body.Add(new CsYield(null));
    }

  }

}
