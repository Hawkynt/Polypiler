using System.Collections.Generic;

namespace Polypiler.Core.Frontends.CSharp;

public static partial class CSharpIntegerApi {

  /// <summary>The conversions of generic math between integer types, of every integer type.</summary>
  private static IEnumerable<CSharpIntegerMember> Conversions => [
    .. OfEveryType("CreateSaturating", "Clamp, Convert | Convert", "an integer outside the target is clamped to its range within its own type, then converted; a floating-point number is converted with saturation"),
    .. OfEveryType("CreateTruncating", "Convert", "the low bits"),
    .. OfEveryType("CreateChecked", "Convert", "traps as an integer overflow outside the target, a NaN included")
  ];

}
