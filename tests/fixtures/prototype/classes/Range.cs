// A positional record class: two components, a computed property, and the value equality the
// compiler gives a record, while ReferenceEquals stays identity. Written for this repository.
#nullable disable
namespace Polypiler.Fixtures.Prototype.Classes;

public sealed record Range(int Lo, int Hi) {

  public int Length => this.Hi - this.Lo;

}

public static class RangeDemo {

  public static bool ValueEqual(int lo, int hi) => new Range(lo, hi) == new Range(lo, hi);

  public static bool Differs(int lo, int hi) => new Range(lo, hi) != new Range(lo, hi + 1);

  public static bool EqualsCall(int lo, int hi) => new Range(lo, hi).Equals(new Range(lo, hi));

  public static bool SameReference(int lo, int hi) => ReferenceEquals(new Range(lo, hi), new Range(lo, hi));

  public static int Length(int lo, int hi) => new Range(lo, hi).Length;

  public static bool NullEqual(int lo) {
    Range missing = null;
    return missing == null && new Range(lo, lo) != missing;
  }

}
