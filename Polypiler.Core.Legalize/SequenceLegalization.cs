using Polypiler.Core.IR;

namespace Polypiler.Core.Legalize;

/// <summary>
/// The operations the lowering of the sequence operations calls (#1329): no target has
/// <see cref="IrIntrinsicOp.StableSort"/> - .NET's <c>Array.Sort</c> and <c>List.Sort</c> are unstable,
/// ECMA-262 required a stable <c>sort</c> only from 2019 on - or <see cref="IrIntrinsicOp.RangeCount"/>,
/// so every backend binds them to the helpers of the same name in the library.
/// </summary>
public static class SequenceLegalization {

  /// <summary>The rewrite that makes each of <see cref="IrSequenceOps.Operations"/> the call of its helper.</summary>
  public static LibraryRewrite Rewrite { get; } = (expression, calls)
    => expression is IrIntrinsic { Op: IrIntrinsicOp.StableSort or IrIntrinsicOp.RangeCount } intrinsic
      ? calls.Call(intrinsic.Op.ToString(), intrinsic.Type, intrinsic.Arguments)
      : null;

}
