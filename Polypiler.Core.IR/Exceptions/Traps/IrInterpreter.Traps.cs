using System.Collections.Generic;

namespace Polypiler.Core.IR;

/// <summary>
/// Thrown by <see cref="IrInterpreter"/> for an explicit trap (<see cref="IrAssert"/>) of a kind no
/// operation of the IR raises with a class of its own (<see cref="TrapKinds.Canonical"/>): the throw of
/// an object of the kind's class (<see cref="TrapKinds.Class"/>) with the kind's message.
/// </summary>
public sealed class IrKindTrapException : IrException {

  internal IrKindTrapException(TrapLabel label)
    : base(IrInterpreter.NewException(TrapKinds.Class(label.Kind), TrapKinds.Message(label.Kind)), TrapKinds.Class(label.Kind), TrapKinds.Message(label.Kind))
    => this.Label = label;

  /// <summary>The kind of the trap.</summary>
  public TrapKind Kind => this.Label!.Kind;

}

public static partial class IrInterpreter {

  private sealed partial class Machine {

    /// <summary>The exceptions whose catch clauses are being tested, the innermost on top, which <see cref="IrTrapMatches"/> looks at.</summary>
    private readonly Stack<IrException> _filtering = new();

    /// <summary>How many variables the operations under a trap attribute have held their operands in.</summary>
    private int _temporaries;

    /// <summary>A trap with a label, as the operation of its kind would trap or with an object of its kind's class.</summary>
    private static IrException Raise(TrapLabel label)
      => TrapKinds.Canonical(label.Kind) is { } trap ? new IrTrapException(trap) { Label = label } : new IrKindTrapException(label);

    /// <summary>An assertion: nothing when its condition holds, its trap otherwise.</summary>
    private Flow? TrapStatement(IrStmt statement, Frame frame) {
      if (statement is not IrAssert assertion)
        return null;

      return (bool)this.Evaluate(assertion.Condition, frame) ? Flow.Next : throw Raise(assertion.Label);
    }

    /// <summary>
    /// An operation under a trap attribute: its operands are evaluated first, as they always are, so
    /// that only a trap of the operation itself takes the attribute's label.
    /// </summary>
    private object? Attributed(IrTrapAttribute attribute, Frame frame) {
      var temporaries = new List<IrLocal>();
      var operation = this.Bound(attribute.Operation, frame, temporaries);
      try {
        return this.Evaluate(operation, frame);
      } catch (IrException thrown) when (thrown.Label?.Kind == attribute.Label.Kind) {
        thrown.Label = attribute.Label;
        throw;
      } finally {
        foreach (var temporary in temporaries)
          frame.Locals.Remove(temporary);
      }
    }

    /// <summary>
    /// The operation with each operand replaced by a variable that holds its value; a step keeps the
    /// place it steps, whose array and index are evaluated.
    /// </summary>
    private IrExpr Bound(IrExpr operation, Frame frame, List<IrLocal> temporaries) {
      IrExpr Hold(IrExpr operand) {
        var temporary = new IrLocal($"\u0001trap{this._temporaries++}", operand.Type);
        frame.Declare(temporary, this.Evaluate(operand, frame));
        temporaries.Add(temporary);
        return new IrLocalRef(temporary);
      }

      if (operation is IrStep step)
        return step.Target is IrArrayLoad element ? step with { Target = new IrArrayLoad(Hold(element.Array), Hold(element.Index)) } : step;

      var operands = IrTree.Operands(operation);
      var held = new List<IrExpr>(operands.Count);
      foreach (var operand in operands)
        held.Add(Hold(operand));

      return IrTree.With(operation, held);
    }

    /// <summary>Whether the trap whose catch clauses are being tested is one a handler with the labels selects.</summary>
    private bool Matches(IrTrapMatches matches) {
      if (this._filtering.Peek().Label is not { } label)
        return false;

      if (matches.Labels.Count == 0)
        return true;

      foreach (var candidate in matches.Labels)
        if (candidate.Selects(label))
          return true;

      return false;
    }

  }

}
