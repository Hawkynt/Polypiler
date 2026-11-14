using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

internal static partial class CSharpIntegerRows {

  /// <summary>
  /// <c>BitOperations.IsPow2</c> and <c>RoundUpToPowerOf2</c> from .NET 6, so the profile .NET 8, on.
  /// <c>IsPow2</c> takes a narrow value as an <c>int</c> and so says false for a negative one;
  /// <c>RoundUpToPowerOf2</c> works on the unsigned pattern, and the cast back keeps the low bits,
  /// which makes 2^width the 0 it wraps to.
  /// </summary>
  private static IEnumerable<CSharpCapability> PowersOfTwo => [
    .. _all.Select(kind => Row(IrIntrinsicOp.IsPow2, kind, CSharpProfile.Net80, Bits + "IsPow2", $"{Bits}IsPow2({{0}})", "{h}({0})",
      IsSigned(kind) ? CSharpIntegerHelpers.IsPow2Signed : CSharpIntegerHelpers.IsPow2Unsigned)),
    .. _all.Select(kind => Row(IrIntrinsicOp.RoundUpPow2, kind, CSharpProfile.Net80, Bits + "RoundUpToPowerOf2",
      $"({Type(kind)}){Bits}RoundUpToPowerOf2({Word(kind)})", $"({Type(kind)}){{h}}({Pattern(kind)})", CSharpIntegerHelpers.RoundUpPow2))
  ];

}
