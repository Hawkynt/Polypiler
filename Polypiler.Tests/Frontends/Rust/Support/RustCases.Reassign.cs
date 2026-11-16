using System.Collections.Generic;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// The calls of the fixture of whole values assigned through <c>&amp;mut</c> (<c>reassign</c>): each
/// kind of place with values that fit and values whose arithmetic overflows before the assignment.
/// </summary>
internal static partial class RustCases {

  private static IReadOnlyList<PrototypeCall> Reassigned() {
    var calls = new List<PrototypeCall>();
    foreach (var k in new byte[] { 4, 0, 253, 254 })
      calls.Add(Call("vec_param", k));

    foreach (var k in new byte[] { 7, 127, 128, 200 })
      calls.Add(Call("arr_param", k));

    foreach (var k in new[] { 7, 0, -3, 1073741823, 2147483647 })
      calls.Add(Call("point_param", k));

    foreach (var k in new byte[] { 3, 1, 255 })
      calls.Add(Call("map_param", k));

    foreach (var k in new byte[] { 9, 0, 1, 100, 201 })
      calls.Add(Call("set_param", k));

    foreach (var k in new byte[] { 2, 253, 254 })
      calls.Add(Call("nested_param", k));

    foreach (var k in new[] { 5, int.MinValue })
      calls.Add(Call("struct_vec", k));

    foreach (var k in new byte[] { 6, 0, 255 })
      calls.Add(Call("locals", k));

    foreach (var k in new byte[] { 250, 0 })
      calls.Add(Call("borrowed_parts", k));

    return calls;
  }

}
