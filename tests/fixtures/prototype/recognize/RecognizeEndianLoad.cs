// O0140: 2, 4 and 8 consecutive byte loads combined by shifts in little- and big-endian order, and
// near misses: a gap, a wrong order, a repeated byte, two arrays. Written for this repository.
namespace Polypiler.Fixtures.Prototype;

public static class RecognizeEndianLoad {

  // expect: O0140
  public static uint Little32(byte[] b, int i) => (uint)(b[i] | b[i + 1] << 8 | b[i + 2] << 16 | b[i + 3] << 24);

  // expect: O0140
  public static uint Big32(byte[] b, int i) => (uint)(b[i] << 24 | b[i + 1] << 16 | b[i + 2] << 8 | b[i + 3]);

  // expect: O0140
  public static ushort Little16(byte[] b, int i) => (ushort)(b[i] | b[i + 1] << 8);

  // expect: O0140
  public static short Big16(byte[] b, int i) => (short)(b[i] << 8 | b[i + 1]);

  // expect: O0140
  public static ulong Little64(byte[] b, int i) => b[i] | (ulong)b[i + 1] << 8 | (ulong)b[i + 2] << 16 | (ulong)b[i + 3] << 24
    | (ulong)b[i + 4] << 32 | (ulong)b[i + 5] << 40 | (ulong)b[i + 6] << 48 | (ulong)b[i + 7] << 56;

  // expect: O0140
  public static long Big64(byte[] b, int i) => (long)b[i] << 56 | (long)b[i + 1] << 48 | (long)b[i + 2] << 40 | (long)b[i + 3] << 32
    | (long)b[i + 4] << 24 | (long)b[i + 5] << 16 | (long)b[i + 6] << 8 | b[i + 7];

  // expect: O0140
  public static int Offset(byte[] b, int i) => b[i + 4] | b[i + 5] << 8 | b[i + 6] << 16 | b[i + 7] << 24;

  // expect: O0140
  public static uint Added(byte[] b, int i) => (uint)(b[i] + (b[i + 1] << 8) + (b[i + 2] << 16) + (b[i + 3] << 24));

  // expect: O0140
  public static int Reversed(byte[] b, int i) => b[i + 3] << 24 | b[i + 2] << 16 | b[i + 1] << 8 | b[i];

  // expect: O0140
  public static uint Constant(byte[] b) => (uint)(b[0] | b[1] << 8 | b[2] << 16 | b[3] << 24);

  // expect: -
  public static uint Gap(byte[] b, int i) => (uint)(b[i] | b[i + 1] << 8 | b[i + 3] << 16 | b[i + 4] << 24);

  // expect: -
  public static uint WrongOrder(byte[] b, int i) => (uint)(b[i + 1] | b[i] << 8 | b[i + 2] << 16 | b[i + 3] << 24);

  // expect: -
  public static uint Repeated(byte[] b, int i) => (uint)(b[i] | b[i] << 8 | b[i + 2] << 16 | b[i + 3] << 24);

  // expect: -
  public static uint TwoArrays(byte[] b, byte[] c, int i) => (uint)(b[i] | c[i + 1] << 8 | b[i + 2] << 16 | b[i + 3] << 24);

  // expect: -
  public static int Partial(byte[] b, int i) => b[i] | b[i + 1] << 8;

  // expect: -
  public static uint Signed(sbyte[] b, int i) => (uint)(b[i] & 0xFF | (b[i + 1] & 0xFF) << 8 | (b[i + 2] & 0xFF) << 16 | (b[i + 3] & 0xFF) << 24);

}
