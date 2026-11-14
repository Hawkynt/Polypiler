// Marsaglia's xorshift32 with the shifts 13, 17 and 5, seeded with 2463534242 ("Xorshift RNGs",
// J. Stat. Softw. 8(14), 2003), and a count of the numbers it has given. The state lives in static
// fields of the class. Written for this repository.
namespace Polypiler.Fixtures.Prototype;

public static class Xorshift32 {

  private static uint _state = 2463534242;
  private static int _calls;

  public static uint Next() {
    _state ^= _state << 13;
    _state ^= _state >> 17;
    _state ^= _state << 5;
    ++_calls;
    return _state;
  }

  public static int Calls() => _calls;

}
