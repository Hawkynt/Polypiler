// A 128-bit unsigned integer as a struct of two 64-bit halves, and its addition with the carry
// from the lower half into the upper one. Written for this repository.
namespace Polypiler.Fixtures.Prototype;

public struct U128 {

  public ulong Lo;
  public ulong Hi;

}

public static class Wide128 {

  public static U128 Add(U128 a, U128 b) {
    var sum = new U128 { Lo = a.Lo + b.Lo };
    sum.Hi = a.Hi + b.Hi + (sum.Lo < a.Lo ? 1UL : 0UL);
    return sum;
  }

  public static ulong AddLo(ulong aLo, ulong aHi, ulong bLo, ulong bHi) => Add(new U128 { Lo = aLo, Hi = aHi }, new U128 { Lo = bLo, Hi = bHi }).Lo;

  public static ulong AddHi(ulong aLo, ulong aHi, ulong bLo, ulong bHi) => Add(new U128 { Lo = aLo, Hi = aHi }, new U128 { Lo = bLo, Hi = bHi }).Hi;

}
