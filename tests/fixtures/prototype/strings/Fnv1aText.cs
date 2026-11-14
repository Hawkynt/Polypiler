// FNV-1a with 32 bits over the UTF-16 code units of a string: each code unit is one step, as a
// foreach and as an indexed loop. Written for this repository.
namespace Polypiler.Fixtures.Prototype;

public static class Fnv1aText {

  private const uint Offset = 2166136261;
  private const uint Prime = 16777619;

  public static uint Hash(string text) {
    uint hash = Offset;
    foreach (char unit in text) {
      hash ^= unit;
      hash *= Prime;
    }

    return hash;
  }

  public static uint HashIndexed(string text) {
    uint hash = Offset;
    for (int index = 0; index < text.Length; index++) {
      hash ^= text[index];
      hash *= Prime;
    }

    return hash;
  }

}
