using System;
using Polypiler.Core.IR;

namespace Polypiler.Core.Legalize;

/// <summary>
/// Replaces the floating-point operations a target has no admitted native call for by calls of their
/// portable implementations, so that a backend only has to write what its target has. The portable
/// implementations come as the module the C# frontend imports from <see cref="FloatOperationSources"/>;
/// the functions a call needs, the ones they call in turn and the tables they read are copied into the
/// module under names no name of the module has, and their own operations are legalized the same way.
/// Generated code stays self-contained, and the interpreter, which runs the same implementations,
/// gives the same bits.
/// </summary>
public static class FloatLegalization {

  /// <summary>
  /// The module with every floating-point intrinsic that has a portable implementation and that
  /// <paramref name="native"/> rejects replaced by a call of that implementation. The operations that
  /// trap on their operands (<c>Clamp</c>, <c>RoundDigits</c>) stay intrinsics: a backend writes them.
  /// </summary>
  /// <param name="module">The module to legalize.</param>
  /// <param name="implementations">The portable implementations as one module.</param>
  /// <param name="native">Whether the target writes an intrinsic itself.</param>
  /// <returns>The legalized module; the module itself when nothing had to be replaced.</returns>
  /// <exception cref="ArgumentException">An implementation the module needs is missing from <paramref name="implementations"/>.</exception>
  public static IrModule Lower(IrModule module, IrModule implementations, Func<IrIntrinsic, bool> native)
    => Lower(module, () => implementations, native);

  /// <summary>
  /// The module legalized as <see cref="Lower(IrModule, IrModule, Func{IrIntrinsic, bool})"/> does, the
  /// portable implementations asked for only when an operation has to be replaced: importing them
  /// compiles their source.
  /// </summary>
  /// <param name="module">The module to legalize.</param>
  /// <param name="implementations">Gives the portable implementations as one module.</param>
  /// <param name="native">Whether the target writes an intrinsic itself.</param>
  /// <returns>
  /// The legalized module, which records every implementation it copied in under
  /// <see cref="IrModule.Implementations"/>; the module itself when nothing had to be replaced.
  /// </returns>
  public static IrModule Lower(IrModule module, Func<IrModule> implementations, Func<IrIntrinsic, bool> native)
    => LibraryLegalization.Lower(module, implementations, Rewrite(native));

  /// <summary>
  /// The rewrite of this legalization, for a backend that composes it with its own: an intrinsic with
  /// a portable implementation that <paramref name="native"/> rejects becomes a call of it, and an
  /// operation of <see cref="IeeeOperations"/>, which no target writes itself, becomes what
  /// <see cref="IeeeLegalization"/> makes of it.
  /// </summary>
  /// <param name="native">Whether the target writes an intrinsic itself.</param>
  /// <returns>The rewrite.</returns>
  public static LibraryRewrite Rewrite(Func<IrIntrinsic, bool> native)
    => (expression, calls) => IeeeLegalization.Call(expression, calls) ?? (expression is IrIntrinsic intrinsic && IsReplaceable(intrinsic) && !native(intrinsic)
      ? calls.Call(Implementation(intrinsic), intrinsic.Type, intrinsic.Arguments)
      : null);

  /// <summary>Whether an intrinsic is one the legalization replaces where the target lacks it.</summary>
  /// <param name="intrinsic">The intrinsic.</param>
  /// <returns><see langword="true"/> for a floating-point operation with a portable implementation that never traps.</returns>
  public static bool IsReplaceable(IrIntrinsic intrinsic)
    => FloatOperationTable.Of(intrinsic.Op) is { DoubleName: not null, Traps: null }
      && intrinsic.Arguments.Count > 0 && intrinsic.Arguments[0].Type.IsFloat;

  /// <summary>The name of the portable implementation of an intrinsic.</summary>
  private static string Implementation(IrIntrinsic intrinsic) {
    var operation = FloatOperationTable.Of(intrinsic.Op)!;
    return intrinsic.Arguments[0].Type == IrType.F32 ? operation.SingleName! : operation.DoubleName!;
  }

}
