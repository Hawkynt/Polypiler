using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.CSharp;

/// <summary>
/// The lazy sequences of C# (#1327): <c>IEnumerable&lt;T&gt;</c> is a restartable sequence; an iterator
/// method - <c>yield return</c>, <c>yield break</c> - is an iterator function, which a call makes the
/// sequence of without running it; a <c>foreach</c> over a sequence is a loop over it, closed however it
/// ends, and one whose body is only <c>yield return</c> of its variable produces the values of the
/// sequence as the iterator's own. An array converts to a sequence of its elements. An enumerator used
/// by hand, an iterator local function or lambda and a method that returns a sequence without being an
/// iterator are refused by name.
/// </summary>
internal sealed partial class Lowering {

  /// <summary>The IR type of <c>IEnumerable&lt;T&gt;</c>; <see langword="null"/> for any other type, a refusal for an enumerator.</summary>
  private IrType? SequenceType(ITypeSymbol? type, SyntaxNode node) {
    // An ordered sequence of LINQ is a sequence as any other.
    var name = type is INamedTypeSymbol { IsGenericType: true, OriginalDefinition.MetadataName: "IOrderedEnumerable`1" } ordered && ordered.ContainingNamespace.ToDisplayString() == "System.Linq"
      ? "IEnumerable`1" : CollectionName(type);
    switch (name) {
      case "IEnumerable`1":
        var sequence = IrType.Sequence(this.Map(((INamedTypeSymbol)type!).TypeArguments[0], node), true);
        return sequence.IsStorable ? sequence : throw new UnsupportedException(node, type.ToDisplayString());
      case "IEnumerator`1":
        throw new UnsupportedException(node, "an enumerator used by hand (GetEnumerator, MoveNext, Current, Reset)");
      default:
        return null;
    }
  }

  /// <summary>A value converted to a sequence: an array as the sequence of its elements; any other value that is no sequence is refused.</summary>
  private IrExpr? ToSequence(IrExpr value, IrType converted, ExpressionSyntax expression) {
    if (!converted.IsLazySequence || value.Type.IsLazySequence)
      return null;

    if (value.Type.IsArray && value.Type.Element == converted.Element)
      return new IrArraySequence(value);

    // A list converts to the sequence of its elements, enumerated as .NET does (#1329).
    if (value.Type.IsList && value.Type.Element == converted.Element)
      return IrSequenceOps.Of(IrSequenceOp.FromList, value);

    throw new UnsupportedException(expression, $"{model.GetTypeInfo(expression).Type?.ToDisplayString()} as an IEnumerable<T>");
  }

  /// <summary>
  /// The statements of iterators: <c>yield return</c> and <c>yield break</c> in an iterator method;
  /// <see langword="false"/> for any other statement.
  /// </summary>
  private bool SequenceStatement(StatementSyntax statement, List<IrStmt> into) {
    switch (statement) {
      case YieldStatementSyntax produced:
        if (model.Inner.GetEnclosingSymbol(produced.SpanStart) is not IMethodSymbol { MethodKind: MethodKind.Ordinary })
          throw new UnsupportedException(produced, "an iterator local function or lambda");

        if (produced.IsKind(SyntaxKind.YieldBreakStatement))
          into.Add(new IrYieldBreak());
        else
          into.Add(new IrYield(this.Initialized(produced.Expression!, into).Value));

        return true;
      default:
        return false;
    }
  }

  /// <summary>Refuses the return of a value from a method that returns a sequence without being an iterator, which runs before the sequence is asked for.</summary>
  private void NoSequenceReturn(ReturnStatementSyntax result) {
    if (result.Expression is { } returned && model.Inner.GetEnclosingSymbol(returned.SpanStart) is IMethodSymbol { MethodKind: MethodKind.Ordinary } method && CollectionName(method.ReturnType) == "IEnumerable`1")
      throw new UnsupportedException(result, "a method that returns an IEnumerable<T> without being an iterator");
  }

  /// <summary>
  /// A <c>foreach</c> over a sequence: a loop over it, whose variable is converted from the element
  /// where the source converts it. In an iterator a loop whose body is only <c>yield return</c> of its
  /// variable yields the values of the sequence. <see langword="null"/> for a loop over anything else.
  /// </summary>
  private IrStmt? SequenceForEach(ForEachStatementSyntax loop) {
    var type = this.Map(model.GetTypeInfo(loop.Expression).Type, loop.Expression);
    if (!type.IsLazySequence)
      return null;

    var sequence = this.Value(loop.Expression);
    var symbol = model.GetDeclaredSymbol(loop)!;
    var itemType = this.Map(symbol.Type, loop.Type);
    var body = loop.Statement is BlockSyntax { Statements: [var only] } ? only : loop.Statement;
    if (body is YieldStatementSyntax { RawKind: (int)SyntaxKind.YieldReturnStatement, Expression: IdentifierNameSyntax produced }
        && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(produced).Symbol, symbol) && itemType == type.Element
        && model.Inner.GetEnclosingSymbol(loop.SpanStart) is IMethodSymbol { MethodKind: MethodKind.Ordinary })
      return new IrYieldFrom(sequence);

    if (itemType == type.Element) {
      var element = this.Local(symbol.Name, itemType);
      this._variables[symbol] = element;
      return new IrForEachSequence(element, sequence, this.Body(loop.Statement));
    }

    // foreach (long v in ints): the value of every pass converted into the variable.
    var raw = this.Local($"$t{this._localBase + this._locals.Count}", type.Element!);
    var item = this.Local(symbol.Name, itemType);
    this._variables[symbol] = item;
    var converted = new IrDeclare(item, Convert(new IrLocalRef(raw), itemType, this.IsChecked(loop)));
    return new IrForEachSequence(raw, sequence, new IrBlock([converted, .. this.Body(loop.Statement).Statements]));
  }

}
