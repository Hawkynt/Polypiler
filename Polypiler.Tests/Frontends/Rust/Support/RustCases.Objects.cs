using System.Collections.Generic;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// The calls of the fixture of trait objects (<c>objects</c>): every kind of object each function
/// makes, the overflow of an impl's arithmetic reached through the interface, and loops of none, one
/// and many objects.
/// </summary>
internal static partial class RustCases {

  private static IReadOnlyList<PrototypeCall> Objects() {
    var calls = new List<PrototypeCall>();
    foreach (var (kind, x) in new (uint, uint)[] { (0, 3), (1, 3), (2, 300), (0, 0), (0, 65535), (0, 70000), (1, 2147483647), (2, uint.MaxValue) })
      calls.Add(Call("boxed_area", kind, x));

    foreach (var (side, w) in new (uint, uint)[] { (2, 5), (0, 0), (65536, 1), (1, 1431655766) })
      calls.Add(Call("borrowed", side, w));

    foreach (var n in new uint[] { 0, 1, 3, 4, 5, 65535, 65536 })
      calls.Add(Call("listed", n));

    foreach (var side in new uint[] { 3, 0, 20000, 65536 })
      calls.Add(Call("shared", side));

    foreach (var n in new uint[] { 7, 0, 4294967, 4294968, uint.MaxValue - 99 })
      calls.Add(Call("tags", n));

    foreach (var (k, times) in new (byte, uint)[] { (0, 3), (3, 3), (9, 2), (9, 0), (1, 1) })
      calls.Add(Call("bumps", k, times));

    foreach (var (k, by) in new (byte, uint)[] { (0, 4), (9, 4), (3, 1073741823), (9, 2147483647), (0, 2147483648) })
      calls.Add(Call("moved", k, by));

    foreach (var (k, x, y) in new (uint, byte, uint)[] { (0, 1, 1), (5, 3, 4), (4, 255, 65535), (5, 3, 40000), (3, 0, 0) })
      calls.Add(Call("picked", k, x, y));

    return calls;
  }

}
