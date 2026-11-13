// CRC-16/CCITT-FALSE: polynomial 0x1021, initial value 0xFFFF, no reflection, no final xor, computed
// bit by bit in a ushort. Written for this repository; the published check value is
// CRC-16/CCITT-FALSE("123456789") = 0x29B1.
namespace Polypiler.Fixtures.Prototype;

public static class Crc16 {

  public static ushort Compute(byte[] data) {
    ushort crc = 0xFFFF;
    for (int i = 0; i < data.Length; ++i) {
      crc ^= (ushort)(data[i] << 8);
      for (int bit = 0; bit < 8; ++bit) {
        if ((crc & 0x8000) != 0)
          crc = (ushort)((crc << 1) ^ 0x1021);
        else
          crc <<= 1;
      }
    }

    return crc;
  }

}
