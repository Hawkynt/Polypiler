using System;

namespace Polypiler.Core.IR;

/// <summary>
/// Thrown by the interpreter where an operation under an <c>[undefined]</c> contract meets operands its
/// contract leaves without a defined result (#122): a result outside the type, a zero divisor under
/// <c>divzero=undefined</c>, <c>MIN / -1</c> under <c>ovf=undefined</c>, an amount not below the width.
/// It is the outcome <c>UndefinedBehaviourReached(instruction)</c> of the reference interpreter: the
/// program has no defined behaviour from that operation on, so no catch of the program sees it - it is
/// no <see cref="IrException"/> - and nothing may rely on a value.
/// </summary>
public sealed class IrUndefinedBehaviourException : Exception {

  /// <summary>Makes the exception for the operation that reached undefined behaviour.</summary>
  /// <param name="node">The operation.</param>
  /// <exception cref="ArgumentNullException"><paramref name="node"/> is <see langword="null"/>.</exception>
  public IrUndefinedBehaviourException(IrIntegerOp node) : base($"undefined behaviour reached at '{(node ?? throw new ArgumentNullException(nameof(node))).Text}'") => this.Node = node;

  /// <summary>The operation that reached undefined behaviour.</summary>
  public IrIntegerOp Node { get; }

}
