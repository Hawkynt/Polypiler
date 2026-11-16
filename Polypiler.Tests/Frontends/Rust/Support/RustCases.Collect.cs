using System.Collections.Generic;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// The calls of the fixture of <c>collect()</c> (<c>collect</c>): empty and filled chains, duplicate
/// keys and elements, the overflow of a function a chain maps with, and text of more than one byte a
/// char.
/// </summary>
internal static partial class RustCases {

  private static IReadOnlyList<PrototypeCall> Collected() {
    var calls = new List<PrototypeCall>();
    foreach (var n in new uint[] { 0, 1, 4, 65536, 70000 })
      calls.Add(Call("squares", n));

    foreach (var (a, b) in new (byte, byte)[] { (2, 4), (1, 3), (0, 255), (254, 1) })
      calls.Add(Call("evens", a, b));

    foreach (var n in new byte[] { 0, 1, 6, 255 })
      calls.Add(Call("index", n));

    foreach (var (a, b) in new (byte, byte)[] { (5, 6), (1, 1), (5, 5), (1, 2) })
      calls.Add(Call("distinct", a, b));

    foreach (var a in new ushort[] { 7, 0, 65532, 65533 })
      calls.Add(Call("borrowed", a));

    foreach (var (a, b) in new (int, int)[] { (2, 3), (0, 0), (214748364, 0), (int.MaxValue, 1), (-107374182, -53687091) })
      calls.Add(Call("paired", a, b));

    foreach (var n in new uint[] { 0, 1, 5 })
      calls.Add(Call("letters", n));

    return calls;
  }

}
