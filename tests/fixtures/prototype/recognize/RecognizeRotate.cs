// O0100: rotations written as shift pairs, and near misses that must stay as they are. Each method
// is preceded by the rule applications expected in it. Written for this repository.
namespace Polypiler.Fixtures.Prototype;

public static class RecognizeRotate {

  // expect: O0100
  public static uint Left8(uint x) => (x << 8) | (x >> 24);

  // expect: O0100
  public static uint RightXor(uint x) => (x >> 7) ^ (x << 25);

  // expect: O0100
  public static uint LeftAdd(uint x) => (x << 13) + (x >> 19);

  // expect: O0100
  public static ulong Left64(ulong x) => (x << 13) | (x >> 51);

  // expect: O0100
  public static int Signed(int x) => (x << 5) | (int)((uint)x >> 27);

  // expect: O0100
  public static uint Variable(uint x, int n) => (x << n) | (x >> (32 - n));

  // expect: O0100
  public static uint Masked(uint x, int n) => (x << (n & 31)) | (x >> (-n & 31));

  // expect: O0100
  public static ulong Variable64(ulong x, int n) => (x >> n) | (x << (64 - n));

  // expect: O0100
  public static ushort Short(ushort x) => (ushort)((x << 3) | (x >> 13));

  // expect: O0100
  public static byte Byte(byte x) => (byte)((x << 3) | (x >> 5));

  // expect: O0100
  public static ushort ShortVariable(ushort x, int n) => (ushort)((x << (n & 15)) | (x >> ((16 - n) & 15)));

  // expect: O0100
  public static byte ByteXor(byte x) => (byte)((x << 1) ^ (x >> 7));

  // expect: O0100
  public static uint OfSum(uint a, uint b) {
    uint t = a + b;
    return (t << 7) | (t >> 25);
  }

  // expect: O0100
  public static uint Statement(uint x) {
    x = (x << 17) | (x >> 15);
    return x;
  }

  // expect: -
  public static uint WrongAmount(uint x) => (x << 8) | (x >> 23);

  // expect: -
  public static int Arithmetic(int x) => (x << 8) | (x >> 24);

  // expect: -
  public static uint TwoValues(uint x, uint y) => (x << 8) | (y >> 24);

  // expect: -
  public static uint XorVariable(uint x, int n) => (x << n) ^ (x >> (32 - n));

  // expect: -
  public static uint AddVariable(uint x, int n) => (x << n) + (x >> (32 - n));

  // expect: -
  public static ushort ShortUnmasked(ushort x, int n) => (ushort)((x << n) | (x >> (16 - n)));

  // expect: -
  public static uint Nothing(uint x) => (x << 0) | (x >> 0);

  // expect: -
  public static uint FromArray(uint[] a, int i) => (a[i] << 8) | (a[i] >> 24);

  // expect: -
  public static uint Quotient(uint x, uint d) => ((x / d) << 8) | ((x / d) >> 24);

}
