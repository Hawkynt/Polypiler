// O0109: sign extension of the low bits by a shift pair and by xor-subtract, and near misses.
// Written for this repository from Warren, Hacker's Delight, 2nd ed., section 2-6.
namespace Polypiler.Fixtures.Prototype;

public static class RecognizeSignExtend {

  // expect: O0109
  public static int ShiftPair(int x) => (x << 20) >> 20;

  // expect: O0109
  public static long ShiftPair64(long x) => (x << 40) >> 40;

  // expect: O0109
  public static int XorSubtract(int x) => ((x & 0xFF) ^ 0x80) - 0x80;

  // expect: O0109
  public static int FromByte(byte b) => (b ^ 0x80) - 0x80;

  // expect: O0109
  public static long XorSubtract64(long x) => ((x & 0xFFFFF) ^ 0x80000) - 0x80000;

  // expect: -
  public static int Unmasked(int x) => (x ^ 0x80) - 0x80;

  // expect: -
  public static uint Logical(uint x) => (x << 20) >> 20;

  // expect: -
  public static int Different(int x) => (x << 20) >> 19;

  // expect: -
  public static int NotAPower(int x) => ((x & 0xFF) ^ 0x81) - 0x81;

}
