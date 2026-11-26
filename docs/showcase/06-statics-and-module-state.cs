// Showcase: Statics & Module State
// Category: Core Language
// Summary: Compile-time constants, read-only static lookup tables, and mutable static module state.
public static class StaticsShowcase {
  private const int Salt = 7;
  private static readonly int[] Weights = [3, 5, 11, 17];
  private static int _counter = 1;

  public static int NextWeighted(int index) {
    int slot = index & 3;
    int value = _counter * Weights[slot] + Salt;
    _counter += 1;
    return value;
  }
}
