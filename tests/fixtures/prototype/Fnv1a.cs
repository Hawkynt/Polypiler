// FNV-1a, 32 bit (Fowler, Noll, Vo): offset basis 0x811C9DC5, prime 0x01000193. Written for this
// repository; the published vectors are FNV-1a("") = 0x811C9DC5 and FNV-1a("a") = 0xE40C292C.
namespace Polypiler.Fixtures.Prototype;

public static class Fnv1a {

  public static uint Hash(byte[] data) {
    uint hash = 0x811C9DC5;
    for (int i = 0; i < data.Length; ++i) {
      hash ^= data[i];
      hash *= 0x01000193;
    }

    return hash;
  }

}
