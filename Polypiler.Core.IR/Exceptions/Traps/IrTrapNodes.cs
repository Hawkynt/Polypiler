using System.Collections.Generic;

namespace Polypiler.Core.IR;

/// <summary>
/// Traps with <paramref name="Label"/> unless <paramref name="Condition"/> is true: the source's
/// <c>assert</c>, a bounds or contract check, and with a false constant an unconditional trap of a
/// kind (<see cref="Fail"/>). A trap happens at its statement: every effect before it is visible, none
/// after it happens. It throws what the operation of its kind would throw
/// (<see cref="TrapKinds.Canonical"/>), or an object of the kind's class, and leaves the statements up to
/// the nearest handler that selects it, as <see cref="IrThrow"/> states.
/// </summary>
/// <param name="Condition">A truth value; the statement traps when it is false.</param>
/// <param name="Label">The label of the trap, built in or declared in the module's trap table.</param>
public sealed record IrAssert(IrExpr Condition, TrapLabel Label) : IrStmt {

  /// <summary>A trap that always happens.</summary>
  /// <param name="label">The label of the trap.</param>
  /// <returns>The statement.</returns>
  public static IrAssert Fail(TrapLabel label) => new(new IrConst(IrType.Bool, 0), label);

}

/// <summary>
/// The attribute <c>[trap &lt;label&gt;]</c> on an operation that can fail: <paramref name="Operation"/>
/// is evaluated as it is, and a trap of the label's kind that the operation raises itself - not one of
/// its operands - carries <paramref name="Label"/>, the source language's name of that failure. A trap
/// of another kind keeps its own label. The operation must be able to fail with the label's kind
/// (<see cref="TrapKinds.Possible"/>).
/// </summary>
/// <param name="Operation">The operation.</param>
/// <param name="Label">The label its traps of the label's kind carry.</param>
public sealed record IrTrapAttribute(IrExpr Operation, TrapLabel Label) : IrExpr(Operation.Type);

/// <summary>
/// Whether the exception a catch clause is testing is a trap a handler with these labels selects: the
/// list is empty, or one of its labels selects the trap's label (<see cref="TrapLabel.Selects"/>). It
/// stands only in the filter of a catch clause; an exception thrown by <see cref="IrThrow"/> is no trap
/// and is selected by none, unless it is a trap caught and thrown again.
/// </summary>
/// <param name="Labels">The labels of the handler; empty selects every trap.</param>
public sealed record IrTrapMatches(IReadOnlyList<TrapLabel> Labels) : IrExpr(IrType.Bool);
