// CRC-32 of ISO 3309 / RFC 1952 section 8, computed bit by bit with the reflected polynomial
// 0xEDB88320. Written for this repository; the check value CRC-32("123456789") = 0xCBF43926 is the
// published one.
namespace Polypiler.Fixtures.Prototype;

public static class Crc32 {

  public static uint Compute(byte[] data) {
    uint crc = 0xFFFFFFFF;
    for (int i = 0; i < data.Length; ++i) {
      crc ^= data[i];
      for (int bit = 0; bit < 8; ++bit)
        crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
    }

    return ~crc;
  }

}
