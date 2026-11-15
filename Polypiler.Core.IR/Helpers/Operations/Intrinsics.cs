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

  /// <summary>The operation <see cref="IrIntrinsicOp.HighWord"/>: the upper 32 bits of a <c>long</c>.</summary>
  /// <param name="value">The integer.</param>
  public static uint IntrinsicHighWord(long value) => (uint)((ulong)value >> 32);

  /// <summary>The operation <see cref="IrIntrinsicOp.JoinWords"/>: the <c>long</c> of a high and a low limb.</summary>
  /// <param name="high">The upper 32 bits.</param>
  /// <param name="low">The lower 32 bits.</param>
  public static long IntrinsicJoinWords(uint high, uint low) => (long)(((ulong)high << 32) | low);

}
