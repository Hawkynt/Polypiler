// CRC-32 of ISO 3309 / RFC 1952 section 8 with the reflected polynomial 0xEDB88320, by a table that
// the first call fills and every later call reuses, guarded by a flag. Written for this repository;
// the check value CRC-32("123456789") = 0xCBF43926 is the published one.
namespace Polypiler.Fixtures.Prototype;

public static class Lazy {

  private static readonly uint[] _table = new uint[256];
  private static bool _filled;

  private static void Fill() {
    for (var n = 0; n < 256; ++n) {
      var c = (uint)n;
      for (var bit = 0; bit < 8; ++bit)
        c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320 : c >> 1;

      _table[n] = c;
    }

    _filled = true;
  }

  public static uint Compute(byte[] data) {
    if (!_filled)
      Fill();

    uint crc = 0xFFFFFFFF;
    for (var i = 0; i < data.Length; ++i)
      crc = _table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);

    return ~crc;
  }

  public static bool Filled() => _filled;

}
