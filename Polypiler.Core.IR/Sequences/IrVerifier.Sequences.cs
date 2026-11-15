using System.Linq;

namespace Polypiler.Core.IR;

public static partial class IrVerifier {

  private sealed partial class FunctionCheck {

    /// <summary>Whether the body being checked is that of the function itself, not of a lambda inside it.</summary>
    private bool InFunctionBody => ReferenceEquals(this._locals, function.Locals);

    /// <summary>
    /// The statements of lazy sequences: a yield of the element type in an iterator function, a yield
    /// break there, a delegation to a sequence of the same element type, and a loop over a sequence
    /// whose element is a local of the function of the element type, in scope in the body only.
    /// <see langword="false"/> for any other statement.
    /// </summary>
    private bool SequenceStatement(IrStmt statement) {
      switch (statement) {
        case IrYield produced:
          this.Expression(produced.Value);
          if (this.Producing())
            this.Require(produced.Value.Type.IsAssignableTo(this._returnType.Element), $"a yield of the type {produced.Value.Type} in an iterator of {this._returnType}");

          return true;
        case IrYieldBreak:
          this.Producing();
          return true;
        case IrYieldFrom delegated:
          this.Expression(delegated.Sequence);
          if (this.Producing())
            this.Require(delegated.Sequence.Type.IsLazySequence && delegated.Sequence.Type.Element!.IsAssignableTo(this._returnType.Element),
              $"a yield of the values of the type {delegated.Sequence.Type} in an iterator of {this._returnType}");

          return true;
        case IrForEachSequence loop:
          this.ForEachSequence(loop);
          return true;
        default:
          return false;
      }
    }

    /// <summary>A statement that produces values stands in an iterator function itself; says whether it does.</summary>
    private bool Producing() {
      var iterator = this._returnType.IsLazySequence && this.InFunctionBody;
      this.Require(iterator, this.InFunctionBody ? "a yield outside an iterator function" : "a yield inside a lambda");
      return iterator;
    }

    /// <summary>A loop over a sequence: its element a local of the function of the element type, in scope in the body only.</summary>
    private void ForEachSequence(IrForEachSequence loop) {
      this.Expression(loop.Sequence);
      var type = loop.Sequence.Type;
      if (!type.IsLazySequence) {
        this.Require(false, $"a loop over the sequence of a value of the type {type}, which is no sequence");
        return;
      }

      this.Require(loop.Element.Type == type.Element, $"the element '{loop.Element.Name}' of a loop over a {type} has the type {loop.Element.Type}");
      this.Require(this._locals.Contains(loop.Element), $"local '{loop.Element.Name}' is not among the locals of the function");
      this.Require(this._visible.Add(loop.Element), $"local '{loop.Element.Name}' is declared twice");
      this.Loop(loop.Body);
      this._visible.Remove(loop.Element);
    }

    /// <summary>An array as a sequence: of an array, a restartable sequence of its element type.</summary>
    private void ArraySequence(IrArraySequence sequence) {
      this.Expression(sequence.Array);
      this.Require(sequence.Array.Type.IsArray, $"a sequence of the elements of a value of the type {sequence.Array.Type}, which is no array");
      this.Require(sequence.Type.IsLazySequence && sequence.Type.Restartable && sequence.Type.Element == sequence.Array.Type.Element,
        $"a sequence of the elements of a {sequence.Array.Type} has the type {sequence.Type}");
    }

    /// <summary>
    /// An operation on lazy sequences: its operands fit it (<see cref="IrSequenceOps.Signature"/>), its
    /// type is the one they give it, a further key follows an ordering and the second sequence of a
    /// <see cref="IrSequenceOp.Zip"/> is an array or a list.
    /// </summary>
    private void SequenceOperation(IrSequence operation) {
      foreach (var argument in operation.Arguments)
        this.Expression(argument);

      var (type, problem) = IrSequenceOps.Signature(operation.Op, [.. operation.Arguments.Select(argument => argument.Type)]);
      if (type is null) {
        this.Require(false, problem!);
        return;
      }

      this.Require(operation.Type == type, $"{operation.Op} has the type {operation.Type}, expected {type}");
      if (operation.Op is IrSequenceOp.ThenBy or IrSequenceOp.ThenByDescending)
        this.Require(operation.Arguments[0] is IrSequence { Op: var before } && IrSequenceOps.IsOrdering(before), $"{operation.Op} of a sequence that is not ordered");

      if (operation.Op == IrSequenceOp.Zip)
        this.Require(operation.Arguments[1] is IrSequence { Op: IrSequenceOp.FromArray or IrSequenceOp.FromList }, "a Zip whose second sequence is no array and no list");
    }

    /// <summary>
    /// Where an iterator function suspends: no yield inside a catch clause, a finally block or the
    /// body of a try that has a catch clause - C# has no form for any of them (CS1626, CS1625, CS1631) -
    /// and no return with a value.
    /// </summary>
    private void Iterator() {
      if (!IrSequences.IsIterator(function))
        return;

      void Walk(IrStmt statement, string? place) {
        switch (statement) {
          case IrYield or IrYieldFrom when place is not null:
            this.Require(false, $"a yield {place}");
            return;
          case IrReturn { Value: not null }:
            this.Require(false, "a return with a value in an iterator function");
            return;
          case IrTry attempt:
            Walk(attempt.Body, place ?? (attempt.Catches.Count > 0 ? "inside a try that has a catch clause" : null));
            foreach (var clause in attempt.Catches)
              Walk(clause.Body, place ?? "inside a catch clause");

            if (attempt.Finally is { } final)
              Walk(final, place ?? "inside a finally block");

            return;
          case IrBlock block:
            foreach (var inner in block.Statements)
              Walk(inner, place);

            return;
          default:
            foreach (var inner in IrTree.Blocks(statement))
              Walk(inner, place);

            return;
        }
      }

      Walk(function.Body, null);
      this.Require(IrSequences.IsElement(function.ReturnType.Element!), $"the iterator returns the type {function.ReturnType}");
    }

  }

}
