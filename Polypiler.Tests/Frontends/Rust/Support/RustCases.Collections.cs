using System.Collections.Generic;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// The calls of the fixture of maps and sets (<c>collections</c>): keys present and absent, the value
/// an insert replaces, indexing an absent key, the overflow of a value written through an entry, and
/// empty inputs.
/// </summary>
internal static partial class RustCases {

  private static IReadOnlyList<PrototypeCall> Collections() {
    var calls = new List<PrototypeCall>();
    foreach (var text in new[] { "banana", "", "xyz", "aé€😀a" })
      calls.Add(Call("letter_counts", text));

    foreach (var (k, v) in new (uint, ulong)[] { (1, 5), (0, 0), (uint.MaxValue, 1000) })
      calls.Add(Call("replaced", k, v));

    foreach (var k in new byte[] { 1, 2, 3 })
      calls.Add(Call("removed", k));

    foreach (var k in new byte[] { 3, 4, 5 })
      calls.Add(Call("indexed", k));

    foreach (var text in new[] { "hello", "", "aaaa", "é😀" })
      calls.Add(Call("distinct", text));

    foreach (var x in new[] { 5L, long.MaxValue, long.MinValue })
      calls.Add(Call("set_ops", x));

    foreach (var text in new[] { "ab cd ab", "", "x y", "ab" })
      calls.Add(Call("word_lengths", text));

    foreach (var n in new uint[] { 0, 3 })
      calls.Add(Call("cleared", n));

    foreach (var k in new[] { true, false })
      calls.Add(Call("points", k));

    foreach (var k in new byte[] { 1, 7 })
      calls.Add(Call("bumped", k));

    return calls;
  }

}
