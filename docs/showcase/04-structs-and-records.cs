// Showcase: Structs & Value Records
// Category: Types & Data
// Summary: Value-type readonly record struct with constructor, copy semantics, and structural equality.
public readonly record struct Vec2(int X, int Y);

public struct MutablePair {
  public int First;
  public int Second;
}

public static class StructsAndRecordsShowcase {
  public static int Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;

  public static bool AreEqual(int x, int y) {
    var left = new Vec2(x, y);
    var right = new Vec2(x, y);
    return left == right;
  }

  public static int CopyValue(int x) {
    var original = new MutablePair { First = x, Second = 10 };
    var copy = original;
    copy.First = x + 1;
    return original.First * 100 + copy.First;
  }
}
