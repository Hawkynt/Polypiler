using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

public static partial class IrVerifier {

  /// <summary>
  /// The trap table of a module (#115): every label it declares has a name a label may have
  /// (<see cref="TrapLabel.Undeclarable"/>) and is declared once; each problem is a
  /// <see cref="TrapLabelRule.Code"/> diagnostic.
  /// </summary>
  private static void Traps(IrModule module, List<Diagnostic> diagnostics) {
    var names = new HashSet<string>();
    foreach (var label in module.Traps) {
      if (TrapLabel.Undeclarable(label.Name) is { } reason)
        diagnostics.Add(TrapLabelRule.Descriptor.At($"{reason}, in the trap table of module '{module.Name}'"));
      else if (!names.Add(label.Name))
        diagnostics.Add(TrapLabelRule.Descriptor.At($"trap label '{label.Name}' is declared twice in module '{module.Name}'"));
    }
  }

  private sealed partial class FunctionCheck {

    /// <summary>An assertion: its condition is a truth value. Whether its label is declared is <see cref="TrapLabelRule"/>'s to check.</summary>
    private bool TrapStatement(IrStmt statement) {
      if (statement is not IrAssert assertion)
        return false;

      this.Value(assertion.Condition, IrType.Bool, "the condition of an assertion");
      return true;
    }

    /// <summary>
    /// A trap attribute, whose operation is checked as it stands, and the test of a handler's labels,
    /// which has no operand; <see langword="false"/> for any other expression.
    /// </summary>
    private bool TrapExpression(IrExpr expression) {
      switch (expression) {
        case IrTrapAttribute attribute:
          this.Expression(attribute.Operation);
          this.Require(attribute.Type == attribute.Operation.Type, $"a trap attribute of the type {attribute.Type} on an operation of the type {attribute.Operation.Type}");
          return true;
        case IrTrapMatches matches:
          this.Require(matches.Labels.Distinct().Count() == matches.Labels.Count, "a handler lists a trap label twice");
          return true;
        default:
          return false;
      }
    }

  }

}
