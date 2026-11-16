using System.Collections.Generic;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// The calls of the fixture of traits and generics (<c>traits</c>): every impl's method and every
/// provided one, each instance of every generic function, the operators a trait impl gives, and the
/// overflow of an instance's arithmetic at the limits of its type.
/// </summary>
internal static partial class RustCases {

  private static IReadOnlyList<PrototypeCall> Traits() {
    var calls = new List<PrototypeCall>();
    foreach (var side in new uint[] { 3, 0, 65535, 65536 })
      calls.Add(Call("square_area", side));

    foreach (var (w, h, k) in new (uint, uint, uint)[] { (2, 3, 4), (0, 9, 9), (65536, 65536, 1), (1, 1, uint.MaxValue) })
      calls.Add(Call("rect_scaled", w, h, k));

    foreach (var side in new uint[] { 1, 7 })
      calls.Add(Call("codes", side));

    foreach (var (side, w) in new (uint, uint)[] { (3, 4), (0, 0), (40000, 1) })
      calls.Add(Call("generic_areas", side, w));

    foreach (var x in new byte[] { 1, 200, 0 })
      calls.Add(Call("doubled_u8", x));

    foreach (var x in new long[] { 3, -5, long.MaxValue / 4, long.MaxValue / 2 })
      calls.Add(Call("doubled_i64", x));

    foreach (var (a, b, c, d) in new (byte, byte, long, long)[] { (1, 2, 3, 4), (9, 2, -3, -4), (5, 5, long.MinValue, long.MinValue) })
      calls.Add(Call("larger_mix", a, b, c, d));

    foreach (var (a, b, c) in new (ushort, ushort, ushort)[] { (1, 2, 3), (65535, 0, 0), (65535, 1, 0), (30000, 30000, 5536) })
      calls.Add(Call("sum3_u16", a, b, c));

    foreach (var a in new[] { 5, int.MaxValue, int.MinValue })
      calls.Add(Call("sum3_i32", a));

    calls.Add(Call("zeros"));
    foreach (var (a, b) in new (int, int)[] { (1, 2), (0, 0), (int.MaxValue, 1), (int.MinValue, 0) })
      calls.Add(Call("vector_sum", a, b));

    foreach (var (a, b) in new (int, int)[] { (1, 2), (3, 3) })
      calls.Add(Call("vector_eq", a, b));

    foreach (var a in new[] { 2, int.MaxValue / 3, int.MaxValue / 2 })
      calls.Add(Call("vector_sum3", a));

    foreach (var side in new uint[] { 5, uint.MaxValue - 100, uint.MaxValue })
      calls.Add(Call("tag_of", side));

    foreach (var side in new uint[] { 4, 65536 })
      calls.Add(Call("describe_square", side));

    calls.Add(Call("unit_sum"));
    return calls;
  }

}
