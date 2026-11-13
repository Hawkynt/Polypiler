// FNV-1a, 64 bit (Fowler, Noll, Vo): offset basis 0xCBF29CE484222325, prime 0x100000001B3. Written for
// this repository; the published vectors are FNV-1a64("") = 0xCBF29CE484222325 and
// FNV-1a64("a") = 0xAF63DC4C8601EC8C.
namespace Polypiler.Fixtures.Prototype;

public static class Fnv1a64 {

  public static ulong Hash(byte[] data) {
    ulong hash = 0xCBF29CE484222325;
    for (int i = 0; i < data.Length; ++i) {
      hash ^= data[i];
      hash *= 0x100000001B3;
    }

    return hash;
  }

}
