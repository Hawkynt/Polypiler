using System.Collections.Generic;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// The calls of the fixture of generic structs, impls, methods and traits (<c>generics</c>): each
/// instance's arithmetic at the limits of its type arguments, the instances a generic method and a
/// generic trait impl are made for, and the exported instances of the public generic function.
/// </summary>
internal static partial class RustCases {

  private static IReadOnlyList<PrototypeCall> Generics() {
    var calls = new List<PrototypeCall>();
    foreach (var (x, y) in new (byte, long)[] { (200, 5), (3, long.MaxValue), (0, long.MinValue + 1), (127, -1) })
      calls.Add(Call("sums", x, y));

    foreach (var (x, y) in new (uint, uint)[] { (1, 2), (0, 0), (429496729, 5), (1, 429496730) })
      calls.Add(Call("swapped", x, y));

    foreach (var x in new ushort[] { 300, 0, 65535 })
      calls.Add(Call("wrapped", x));

    foreach (var x in new byte[] { 4, 0, 255 })
      calls.Add(Call("areas", x));

    foreach (var x in new byte[] { 3, 255 })
      calls.Add(Call("paired", x));

    foreach (var x in new[] { 1, 2, int.MinValue })
      calls.Add(Call("equal", x));

    foreach (var (a, b) in new (int, int)[] { (3, 4), (0, -1), (536870911, 0), (536870912, 0), (-536870913, 7) })
      calls.Add(Call("vectors", a, b));

    foreach (var a in new byte[] { 100, 126, 127, 200 })
      calls.Add(Call("nested", a));

    foreach (var a in new ushort[] { 5, 65535 })
      calls.Add(Call("boxed", a));

    foreach (var n in new byte[] { 9, 0, 253, 254, 255 })
      calls.Add(Call("listed", n));

    foreach (var (x, y) in new (byte, sbyte)[] { (5, 7), (0, 0), (255, -128), (255, 127) })
      calls.Add(Call("conversions", x, y));

    foreach (var x in new byte[] { 2, 0, 255 })
      calls.Add(Call("tagged", x));

    foreach (var (a, b, c) in new (byte, byte, long)[] { (3, 9, -5), (9, 3, long.MaxValue), (0, 0, long.MinValue) })
      calls.Add(Call("largest_user", a, b, c));

    foreach (var (a, b) in new (byte, byte)[] { (4, 2), (0, 255) })
      calls.Add(Call("largest_u8", a, b));

    foreach (var (a, b) in new (long, long)[] { (-4, 2), (long.MinValue, long.MaxValue), (3, 3) })
      calls.Add(Call("largest_i64", a, b));

    return calls;
  }

}
