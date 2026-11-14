// Marsaglia's xorshift64 with the shifts 13, 7 and 17, seeded with 88172645463325252 ("Xorshift
// RNGs", J. Stat. Softw. 8(14), 2003): 64 bits of state in a static field. Written for this repository.
namespace Polypiler.Fixtures.Prototype;

public static class Xorshift64State {

  private static ulong _state = 88172645463325252;

  public static ulong Next() {
    _state ^= _state << 13;
    _state ^= _state >> 7;
    _state ^= _state << 17;
    return _state;
  }

}
