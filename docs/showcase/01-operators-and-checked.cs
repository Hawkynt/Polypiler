// Showcase: Operators, Bitwise & Checked Arithmetic
// Category: Core Language
// Summary: Wrapping vs checked integer arithmetic, bitwise masks, shifts, and bit rotation.
using System.Numerics;

public static class OperatorsShowcase {
  public static uint Mix(uint value, int shift) {
    uint rotated = BitOperations.RotateLeft(value, shift);
    return unchecked((rotated ^ 0x9E3779B9u) * 0x85EBCA6Bu);
  }

  public static int SafeAdd(int a, int b) => checked(a + b);
}
