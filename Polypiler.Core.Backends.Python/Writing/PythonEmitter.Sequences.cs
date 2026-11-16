using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Python.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Python;

public static partial class PythonEmitter {

  private sealed partial class Writer {

    /// <summary>Whether a block produces values with <c>yield</c>, outside the lambdas in it.</summary>
    private static bool Yields(IrBlock body) => Own(body).Any(statement => statement is IrYield or IrYieldBreak or IrYieldFrom);

    /// <summary>Every statement of a block, at any depth, those of its lambdas left out.</summary>
    private static IEnumerable<IrStmt> Own(IrStmt statement) {
      yield return statement;
      foreach (var block in statement is IrBlock own ? [own] : IrTree.Blocks(statement))
        foreach (var inner in block.Statements)
          foreach (var nested in Own(inner))
            yield return nested;
    }

    /// <summary>
    /// The body of an iterator function. A single-pass sequence is a generator function itself; a
    /// restartable one returns a sequence object that calls a nested generator function anew on every
    /// enumeration, with the argument values of the call. <see langword="null"/> for any other function.
    /// </summary>
    private List<PyStatement>? IteratorBody(IrFunction function) {
      if (!function.ReturnType.IsLazySequence || !Yields(function.Body))
        return null;

      if (!function.ReturnType.Restartable)
        return this.Statements(function.Body.Statements);

      var make = this.Temporary("_make");
      var parameters = function.Parameters.Where(parameter => parameter != this._receiver).ToList();
      var body = this.Statements(function.Body.Statements);
      var inner = new PyFunctionDef(make, [.. parameters.Select(parameter => new PyParameter(this.LocalName(parameter), this.Annotation(parameter.Type)))],
        new PySubscript(Name("Iterator"), this.Annotation(function.ReturnType.Element!)), body);
      return [inner, new PyReturn(this.Helper(PythonHelpers.Sequence, [Name(make), .. parameters.Select(parameter => Name(this.LocalName(parameter)))]))];
    }

    /// <summary>The statements of lazy sequences; <see langword="false"/> for any other.</summary>
    private bool SequenceStatement(IrStmt statement, List<PyStatement> written) {
      switch (statement) {
        case IrYield produced:
          written.Add(new PyExpressionStatement(new PyYield(this.Expression(produced.Value), false)));
          return true;
        case IrYieldBreak:
          written.Add(new PyReturn(null));
          return true;
        case IrYieldFrom delegated:
          written.Add(new PyExpressionStatement(new PyYield(this.Expression(delegated.Sequence), true)));
          return true;
        case IrForEachSequence loop:
          var sequence = this.Expression(loop.Sequence);
          var frame = new LoopFrame(null, null);
          // The one cursor of a single-pass sequence is closed however the loop ends, which runs the
          // generator's pending finally blocks; Python closes it only when nothing holds it any more.
          string? cursor = null;
          if (!loop.Sequence.Type.Restartable) {
            cursor = this.Temporary("_cursor");
            written.Add(new PyAssign(Name(cursor), sequence));
            sequence = Name(cursor);
          }

          PyStatement walk;
          if (this.IsCell(loop.Element)) {
            var item = this.Temporary("_item");
            walk = new PyFor(item, sequence, [this.Declare(loop.Element, Name(item)), .. this.Inside(frame, loop.Body.Statements)]);
          } else {
            var name = this.LocalName(loop.Element);
            this._annotated.Add(name);
            walk = new PyFor(name, sequence, this.Inside(frame, loop.Body.Statements));
          }

          written.Add(cursor is null ? walk : new PyTry([walk], [], [new PyExpressionStatement(new PyCall(new PyAttribute(Name(cursor), "close"), []))]));
          return true;
        case IrForEachCollection loop:
          this.ForEachCollection(loop, written);
          return true;
        default:
          return false;
      }
    }

  }

}
