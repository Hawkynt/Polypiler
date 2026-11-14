using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Frontends.CSharp;

public static partial class CSharpIntegerApi {

  private const string Crossed = "crossed bounds trap as an invalid operation, where .NET throws ArgumentException";

  /// <summary>Bounds of integers: <c>Math.Clamp</c> and the minimum, maximum, magnitude, sign and clamp of every integer type; <c>Math.Min</c>, <c>Max</c>, <c>Abs</c> and <c>Sign</c> are the frontend's own.</summary>
  private static IEnumerable<CSharpIntegerMember> Bounds => [
    new(Math, "Clamp", "Clamp", Crossed),
    .. OfEveryType("Clamp", "Clamp", Crossed),
    .. OfEveryType("Min", "Min"),
    .. OfEveryType("Max", "Max"),
    .. OfEveryType("Abs", "Abs").Where(member => member.Type is "sbyte" or "short" or "int" or "long"),
    .. OfEveryType("Sign", "Sign")
  ];

}
