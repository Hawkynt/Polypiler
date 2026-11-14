using System.Collections.Generic;

namespace Polypiler.Core.Frontends.CSharp;

public static partial class CSharpIntegerApi {

  /// <summary>The counts of bits: of <c>BitOperations</c>, and of every integer type, whose result has the type of the value.</summary>
  private static IEnumerable<CSharpIntegerMember> Counts => [
    new(Bits, "PopCount", "PopCount"),
    new(Bits, "LeadingZeroCount", "LeadingZeroCount"),
    new(Bits, "TrailingZeroCount", "TrailingZeroCount"),
    new(Bits, "Log2", "Log2"),
    .. OfEveryType("PopCount", "PopCount"),
    .. OfEveryType("LeadingZeroCount", "LeadingZeroCount"),
    .. OfEveryType("TrailingZeroCount", "TrailingZeroCount"),
    .. OfEveryType("Log2", "Log2", "of a signed type: a negative value, for which .NET throws ArgumentOutOfRangeException, traps as an integer overflow")
  ];

}
