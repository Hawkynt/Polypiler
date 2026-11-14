using System.Collections.Generic;

namespace Polypiler.Core.Frontends.CSharp;

public static partial class CSharpIntegerApi {

  /// <summary>The rotates of <c>BitOperations</c> and of every integer type.</summary>
  private static IEnumerable<CSharpIntegerMember> Rotates => [
    new(Bits, "RotateLeft", "RotateLeft"),
    new(Bits, "RotateRight", "RotateRight"),
    .. OfEveryType("RotateLeft", "RotateLeft"),
    .. OfEveryType("RotateRight", "RotateRight")
  ];

}
