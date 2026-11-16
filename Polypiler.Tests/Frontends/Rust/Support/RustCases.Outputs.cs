using System.Collections.Generic;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// The calls of the fixture of associated types of type parameters (<c>outputs</c>): each instance's
/// result at the limits of the type its <c>Output</c> is.
/// </summary>
internal static partial class RustCases {

  private static IReadOnlyList<PrototypeCall> Outputs() {
    var calls = new List<PrototypeCall>();
    foreach (var (x, y) in new (byte, long)[] { (100, 5), (127, 0), (128, -1), (255, long.MaxValue), (0, long.MinValue) })
      calls.Add(Call("sums", x, y));

    foreach (var x in new[] { 3, int.MaxValue - 2, int.MaxValue - 1, int.MinValue })
      calls.Add(Call("literal", x));

    foreach (var x in new ushort[] { 7, 0, 32767, 32768, 65535 })
      calls.Add(Call("doubled", x));

    return calls;
  }

}
