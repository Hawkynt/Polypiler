// SPDX-License-Identifier: MIT
namespace Polypiler.Core.IR;

/// <summary>
/// The operations of the IR that C# has no method for, as the helpers call them: each method is
/// imported as its operation (<see cref="HelperOperationSources.Intrinsics"/>), never as a function,
/// and its body computes the operation in .NET, so that the helpers run as ordinary C# as well.
/// </summary>
public static partial class HelperOperations {

  /// <summary>The operation <see cref="IrIntrinsicOp.RoundHalfUp"/>: ECMA-262's <c>Math.round</c>.</summary>
  /// <param name="value">The number.</param>
  public static double IntrinsicRoundHalfUp(double value) => RoundHalfUp(value);

}
