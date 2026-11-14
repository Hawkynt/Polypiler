using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>
/// Every floating-point operation of the IR with its shape, contract, special values and portable
/// implementation (ADR 0005). Each family registers its operations here with one entry, so the
/// typing rules, the interpreter, the backends and the reference page all read one table.
/// </summary>
public static class FloatOperationTable {

  /// <summary>The operations, family by family.</summary>
  public static IReadOnlyList<FloatOperation> All { get; } = [
    .. ExactFamilies.Operations,
    .. RootsFamily.Operations,
    .. TrigonometricFamily.Operations,
    .. HyperbolicFamily.Operations,
    .. ExponentialFamily.Operations,
    .. LogarithmFamily.Operations,
    .. InverseTrigonometricFamily.Operations,
    .. InverseHyperbolicFamily.Operations,
    .. PowerFamily.Operations
  ];

  private static readonly Dictionary<IrIntrinsicOp, FloatOperation> _byOp = All.ToDictionary(operation => operation.Op);

  /// <summary>The operation of the table, or <see langword="null"/> when the table has none for it.</summary>
  /// <param name="op">The operation.</param>
  /// <returns>Its entry.</returns>
  public static FloatOperation? Of(IrIntrinsicOp op) => _byOp.GetValueOrDefault(op);

  /// <summary>
  /// The typing rule of a floating-point operation the table introduced, or <see langword="null"/> for
  /// an operation whose rule <see cref="IrOperators"/> keeps: one the IR had before the table, or one
  /// the table does not hold.
  /// </summary>
  /// <param name="op">The operation.</param>
  /// <param name="result">The type of the node.</param>
  /// <param name="arguments">The types of the arguments.</param>
  /// <returns>Whether the types fit, or <see langword="null"/>.</returns>
  public static bool? Accepts(IrIntrinsicOp op, IrType result, IReadOnlyList<IrType> arguments) {
    if (Of(op) is not { Double: not null } operation || arguments.Count == 0 || !arguments[0].IsFloat)
      return null;

    return operation.Accepts(result, arguments);
  }

  /// <summary>What is wrong with a node beyond its types: the mode of a rounding to digits has to be a constant mode.</summary>
  /// <param name="intrinsic">The node.</param>
  /// <returns>The problem, or <see langword="null"/>.</returns>
  public static string? Problem(IrIntrinsic intrinsic)
    => intrinsic.Op == IrIntrinsicOp.RoundDigits && intrinsic.Arguments.Count == 3
      && intrinsic.Arguments[2] is not IrConst { Value: >= FloatOperations.RoundToEven and <= FloatOperations.RoundToPositiveInfinity }
        ? "the mode of RoundDigits is not a constant of MidpointRounding"
        : null;

  /// <summary>
  /// Evaluates a floating-point operation of the table on the values the interpreter carries, when the
  /// table has the implementation for it: an <c>i32</c> is a <see cref="long"/>, a truth value a
  /// <see cref="bool"/>.
  /// </summary>
  /// <param name="op">The operation.</param>
  /// <param name="arguments">The evaluated arguments.</param>
  /// <param name="result">The result.</param>
  /// <returns><see langword="true"/> when the table evaluated the operation.</returns>
  /// <exception cref="IrTrapException">The operands make the operation trap.</exception>
  public static bool TryEvaluate(IrIntrinsicOp op, object[] arguments, out object? result) {
    result = null;
    if (Of(op) is not { Double: { } evaluate64, Single: { } evaluate32 } operation || arguments.Length == 0 || arguments[0] is not (float or double))
      return false;

    if (operation.Traps is { } traps && traps(arguments))
      throw new IrTrapException(IrTrap.InvalidOperation);

    result = arguments[0] is float ? evaluate32(arguments) : evaluate64(arguments);
    return true;
  }

}
