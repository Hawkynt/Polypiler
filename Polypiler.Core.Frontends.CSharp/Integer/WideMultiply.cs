using System.Collections.Generic;

namespace Polypiler.Core.Frontends.CSharp;

public static partial class CSharpIntegerApi {

  /// <summary>The full product of <c>Math.BigMul</c>: of two 32-bit integers, and of two 64-bit ones as the upper half returned and the lower half in its out argument.</summary>
  private static IEnumerable<CSharpIntegerMember> WideMultiplies => [
    new(Math, "BigMul", "BigMul | MulHigh, Multiply", "of two 64-bit integers only as a statement - a declaration, an assignment or a call - whose out argument is a local: the upper half MulHigh, the lower one Multiply")
  ];

}
