// Fletcher's checksum with two 8-bit sums modulo 255, returned as sum2 * 256 + sum1. Written for this
// repository; the sums are kept in ushort variables, so every step narrows from int.
namespace Polypiler.Fixtures.Prototype;

public static class Fletcher16 {

  public static ushort Compute(byte[] data) {
    ushort sum1 = 0;
    ushort sum2 = 0;
    for (int i = 0; i < data.Length; ++i) {
      sum1 = (ushort)((sum1 + data[i]) % 255);
      sum2 = (ushort)((sum2 + sum1) % 255);
    }

    return (ushort)((sum2 << 8) | sum1);
  }

}
