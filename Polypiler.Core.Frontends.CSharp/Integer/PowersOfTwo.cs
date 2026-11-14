using System.Collections.Generic;

namespace Polypiler.Core.Frontends.CSharp;

public static partial class CSharpIntegerApi {

  /// <summary>The powers of two of <c>BitOperations</c> and of every integer type.</summary>
  private static IEnumerable<CSharpIntegerMember> Powers => [
    new(Bits, "IsPow2", "IsPow2"),
    new(Bits, "RoundUpToPowerOf2", "RoundUpPow2"),
    .. OfEveryType("IsPow2", "IsPow2")
  ];

}
