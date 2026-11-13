// SplitMix64 (Steele, Lea, Flood; the constants as Vigna publishes them): the state advances by
// 0x9E3779B97F4A7C15 and is mixed with 0xBF58476D1CE4E5B9 and 0x94D049BB133111EB. Written for this
// repository; seeded with 0 the first output is 0xE220A8397B1DCDAF.
namespace Polypiler.Fixtures.Prototype;

public static class SplitMix64 {

  public static ulong First(ulong seed) => Mix(seed + 0x9E3779B97F4A7C15);

  public static ulong Nth(ulong seed, int n) {
    ulong state = seed;
    ulong output = 0;
    for (int i = 0; i < n; ++i) {
      state += 0x9E3779B97F4A7C15;
      output = Mix(state);
    }

    return output;
  }

  private static ulong Mix(ulong z) {
    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
    z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
    return z ^ (z >> 31);
  }

}
