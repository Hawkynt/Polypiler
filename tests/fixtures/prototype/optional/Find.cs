// Nullable values and null references: a search that returns int?, the lifted operators of C#,
// ??, ??= and ?. on an array that may be null, .Value of none, and the single evaluation of the
// receiver of ?. and of the left side of ??. Written for this repository.
#nullable enable
namespace Polypiler.Fixtures.Prototype.Optional;

public sealed class Box {

  public int V;

  public Box(int v) {
    this.V = v;
  }

}

public static class OptionalDemo {

  public static int? Find(int[] a, int v) {
    for (var i = 0; i < a.Length; ++i)
      if (a[i] == v)
        return i;

    return null;
  }

  public static int FindOr(int[] a, int v) => Find(a, v) ?? -1;

  public static int Next(int? a) => (a + 1) ?? -1;

  public static bool Less(int? a) => a < 1;

  public static bool IsNull(int? a) => a == null;

  public static int Or5(int? z) => z ?? 5;

  public static int Assigned() {
    int? q = null;
    q ??= 7;
    q ??= 9;
    return q.Value;
  }

  public static int AssignedValue(int? q) => (q ??= 4) + 10 * (q ?? 0);

  public static int Length(int[]? a) => a?.Length ?? -1;

  public static int Element(int[]? a, int i) => a?[i] ?? -1;

  public static bool IsMissing(int[]? a) => a is null;

  public static bool IsThere(int[]? a) => a != null && a is not null;

  public static int ValueOfNone() {
    int? e = null;
    return e!.Value;
  }

  public static bool NaNEqualsItself(double x) {
    double? n = x;
    var same = n;
    return n == same;
  }

  public static int Lifted(int? a, int? b) => (a * b - a) ?? -100;

  public static bool Equal(int? a, int? b) => a == b;

  public static bool NotEqual(int? a, int? b) => a != b;

  public static bool Greater(int? a, int? b) => a > b;

  public static bool AtMost(int? a, int b) => a <= b;

  public static int Negated(int? a) => (-a) ?? 0;

  public static int Complement(int? a) => (~a) ?? 0;

  public static bool Inverted(bool? b) => (!b) ?? false;

  public static long Widened(int? a) => (a + 1L) ?? -1L;

  public static int Shifted(int? a, int s) => (a << s) ?? -1;

  public static int Masked(uint? a) => (int)((a & 0xFFu) ?? 1000u);

  public static int HasValue(int? a) => a.HasValue ? a.Value : a.GetValueOrDefault(3);

  public static int OrDefault(int? a) => a.GetValueOrDefault();

  public static double Halved(double? d) => (d / 2) ?? double.NaN;

  public static int Cast(long? a) => (int?)a ?? -1;

  public static int Unwrapped(int? a) => (int)a!;

  public static int Chosen(bool present) {
    int? value = present ? 4 : null;
    return value ?? -4;
  }

  private static int[]? Produce(int[] calls, bool present) {
    calls[0] += 1;
    return present ? new int[2] : null;
  }

  private static int? NextValue(int[] calls, bool present) {
    calls[0] += 1;
    return present ? 4 : null;
  }

  private static int Other(int[] calls) {
    calls[0] += 100;
    return -1;
  }

  public static int CountedLength(int[] calls, bool present) => Produce(calls, present)?.Length ?? -1;

  public static int CountedCoalesce(int[] calls, bool present) => NextValue(calls, present) ?? Other(calls);

  public static int CountedLifted(int[] calls, bool present) => (NextValue(calls, present) + NextValue(calls, !present)) ?? -calls[0];

  public static bool CountedLess(int[] calls, bool present) => NextValue(calls, present) < NextValue(calls, present);

  public static int Operators(int? a, int? b)
    => ((a % b) ?? -1) + ((a | b) ?? -2) * 3 + ((a ^ b) ?? -3) * 5 + ((a >> 1) ?? -4) * 7 + ((a >>> 1) ?? -5) * 11 + (a >= b ? 13 : 0);

  public static int ConstantLeft(int? a) => (5 - a) ?? -1;

  private static int Twice(int[] calls, int x) {
    calls[0] += 1;
    return 2 * x;
  }

  public static int CallLeft(int[] calls, int? a) => (Twice(calls, 3) - a) ?? -calls[0];

  public static bool IsFive(int? a) => a == 5;

  public static int OtherArray(int[]? a, int[] b) => (a ?? b).Length;

  public static int BoxedOr(int v, bool present) {
    Box? b = present ? new Box(v) : null;
    return b?.V ?? -1;
  }

  public static int AssignedArray(int[]? a) => (a ??= new int[2]).Length;

  public static int AssignedArrayStatement(int[]? a) {
    a ??= new int[3];
    return a.Length;
  }

  public static bool CountedEqual(int[] calls, bool present) => NextValue(calls, present) == NextValue(calls, !present);

}
