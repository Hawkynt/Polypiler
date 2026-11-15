// FNV-1a, 32 bit (Fowler, Noll, Vo), over a pinned array walked by a pointer: offset basis 0x811C9DC5,
// prime 0x01000193. Written for this repository; the published vectors are FNV-1a("") = 0x811C9DC5 and
// FNV-1a("a") = 0xE40C292C.
namespace Polypiler.Fixtures.Prototype;

public static unsafe class Fnv1aPointer {

  public static uint Hash(byte[] data) {
    uint hash = 0x811C9DC5;
    fixed (byte* start = data) {
      byte* p = start;
      byte* end = start + data.Length;
      while (p != end) {
        hash ^= *p;
        hash *= 0x01000193;
        p++;
      }
    }

    return hash;
  }

}
