using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// The calls of the fixtures of arrays, slices and Vec (<c>memory</c>), of structs, enums, Option and
/// Result (<c>types</c>) and of closures and iterator closures (<c>closures</c>): the values the issues
/// state, each boundary of each limit - empty slices, the length as an index, a range ending at the
/// length, chunk remainders, zip lengths, the type limits - and the inputs that panic.
/// </summary>
internal static partial class RustCases {

  private static byte[] Bytes(string text) => [.. text.Select(character => (byte)character)];

  private static IReadOnlyList<PrototypeCall> Memory() {
    var calls = new List<PrototypeCall>();
    foreach (var data in new[] { Bytes("Wikipedia"), [], [255], Enumerable.Repeat((byte)255, 1000).ToArray() })
      calls.Add(Call("adler32", data));

    foreach (var data in new[] { Bytes("123456789"), [], new byte[] { 0 } })
      calls.Add(Call("crc32_bitwise", data));

    foreach (var (source, target) in new (byte[], int)[] { ([0x00, 0xFF, 0x5A, 0x01], 5), ([1, 2], 2), ([1, 2, 3], 1), ([], 2) })
      calls.Add(Call("xor_transform", source, new byte[target]));

    foreach (var data in new uint[][] { [3, 1, 4], [], [0, uint.MaxValue, 3], [7] })
      calls.Add(Call("weighted_sum", data));

    foreach (var data in new byte[][] { [0x12, 0x34, 0x56, 0x78], [1, 2, 3], [0xFF, 0xFF, 0xFF, 0xFF, 0] })
      calls.Add(Call("load_be32", data));

    foreach (var at in new ulong[] { 0, 1, 2, ulong.MaxValue })
      calls.Add(Call("load_le16", new byte[] { 1, 2, 3 }, at));

    foreach (var (value, length) in new (uint, int)[] { (0x12345678, 4), (1, 6), (1, 3) })
      calls.Add(Call("store_le32", value, new byte[length]));

    foreach (var name in new[] { "reverse_in_place", "reversed" })
      foreach (var data in new int[][] { [1, 2, 3, 4], [], [5], [1, 2, 3] })
        calls.Add(Call(name, data));

    foreach (var length in new[] { 0, 1, 6, 7 })
      calls.Add(Call("chunk_heads", Enumerable.Range(1, length).Select(value => (byte)value).ToArray()));

    foreach (var data in new byte[][] { [1, 2, 3, 4, 5], [255, 1], [] })
      calls.Add(Call("chunk_sums", data));

    foreach (var data in new byte[][] { [10, 20, 30, 40], [1, 2] })
      calls.Add(Call("mid_sum", data));

    foreach (var index in new ulong[] { 0, 2, 3, ulong.MaxValue })
      calls.Add(Call("at", new byte[] { 1, 2, 3 }, index));

    foreach (var (i, j) in new (ulong, ulong)[] { (3, 3), (2, 1), (1, 5), (0, 3), (0, 0) })
      calls.Add(Call("tail_len", new byte[] { 1, 2, 3 }, i, j));

    foreach (var (i, j) in new (ulong, ulong)[] { (0, 2), (1, 3), (2, 1), (3, 2), (0, ulong.MaxValue) })
      calls.Add(Call("inclusive_len", new byte[] { 1, 2, 3 }, i, j));

    foreach (var i in new ulong[] { 0, 3, 4 })
      calls.Add(Call("from_len", new byte[] { 1, 2, 3 }, i));

    calls.Add(Call("put", new byte[3], new byte[] { 1, 2 }));
    calls.Add(Call("put", new byte[2], new byte[] { 1, 2 }));
    foreach (var (from, to, destination) in new (ulong, ulong, ulong)[] { (0, 3, 2), (2, 1, 0), (0, 5, 1), (1, 3, 3), (0, 0, 5) })
      calls.Add(Call("shift_within", new byte[] { 1, 2, 3, 4, 5 }, from, to, destination));

    foreach (var middle in new ulong[] { 2, 0, 4, 5 })
      calls.Add(Call("split_and_write", new uint[] { 1, 2, 3, 4 }, middle));

    calls.Add(Call("fill_with", new ushort[] { 1, 2, 3 }, (ushort)9));
    calls.Add(Call("fill_with", new ushort[0], (ushort)1));
    foreach (var data in new byte[][] { [200, 100], [1, 2], [] })
      calls.Add(Call("bsum", data));

    foreach (var data in new byte[][] { [], [1, 2] })
      calls.Add(Call("last_element", data));

    foreach (var (index, divisor) in new (ulong, byte)[] { (9, 0), (9, 2), (1, 2) })
      calls.Add(Call("assign_order", new byte[3], index, divisor));

    foreach (var data in new long[][] { [long.MaxValue, 1], [-3, 4], [] })
      calls.Add(Call("wrapping_total", data));

    foreach (var data in new double[][] { [1.5, 2.25], [], [double.NaN, 1], [0.1, 0.2] })
      calls.Add(Call("float_total", data));

    foreach (var data in new uint[][] { [1, 2, 3, 4, 5, 6, 7], [], [5] })
      calls.Add(Call("adapters", data));

    foreach (var (left, right) in new (int, int)[] { (3, 3), (2, 3), (3, 2), (0, 3) })
      calls.Add(Call("zip_count", new byte[left], new byte[right]));

    foreach (var n in new uint[] { 0, 5, 1 })
      calls.Add(Call("vec_squares", n));

    foreach (var n in new ulong[] { 0, 3 })
      calls.Add(Call("vec_macros", n));

    foreach (var i in new ulong[] { 0, 7, 8 })
      calls.Add(Call("table", i));

    foreach (var x in new uint[] { 1, uint.MaxValue - 1 })
      calls.Add(Call("array_copy", x));

    foreach (var x in new uint[] { 0x12345678, 0 })
      calls.Add(Call("byte_order", x));

    foreach (var x in new uint[] { 5, uint.MaxValue })
      calls.Add(Call("scalar_refs", x));

    foreach (var n in new ulong[] { 0, 4, 9, 10 })
      calls.Add(Call("bytes_sum", n));

    return calls;
  }

  private static IReadOnlyList<PrototypeCall> Types() {
    var calls = new List<PrototypeCall>();
    foreach (var seed in new uint[] { 2463534242, 1, uint.MaxValue })
      calls.Add(Call("xorshift_two", seed));

    foreach (var (x, y, d) in new (int, int, int)[] { (3, -4, 2), (0, 0, 0), (int.MaxValue, 0, 1), (-5, 7, -3) })
      calls.Add(Call("point_ops", x, y, d));

    foreach (var (a, b, c) in new (int, int, int)[] { (100, 5, 2), (1, 0, 2), (-1, 1, 1), (10, 2, 0), (int.MinValue, -1, 1) })
      calls.Add(Call("code", a, b, c));

    foreach (var (program, start) in new (byte[], int)[] { ([1, 5, 6], 10), ([6, 255, 1], 2), ([1, 2], 0), ([], -7), ([6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6], 3) })
      calls.Add(Call("run", program, start));

    foreach (var x in new[] { long.MinValue, -1L, 0L, 1L })
      calls.Add(Call("sign_of", x));

    foreach (var data in new byte[][] { [1, 2, 3], [1, 2] }) {
      calls.Add(Call("third", data));
      calls.Add(Call("must", data));
    }

    foreach (var n in new uint[] { 3, 0, 1 })
      calls.Add(Call("popsum", n));

    foreach (var data in new uint[][] { [4], [], [uint.MaxValue] })
      calls.Add(Call("first_plus_one", data));

    foreach (var data in new uint[][] { [4, 9], [] })
      calls.Add(Call("last_or", data, 77u));

    foreach (var x in new uint[] { 8, 3, 0, uint.MaxValue, 12 })
      calls.Add(Call("options", x));

    foreach (var x in new uint[] { 12, 6, 7, 0 }) {
      calls.Add(Call("quarter_or", x));
      calls.Add(Call("expect_even", x));
    }

    foreach (var (a, b) in new (int, int)[] { (10, 2), (10, 0), (-4, 2) })
      calls.Add(Call("result_methods", a, b));

    foreach (var x in new[] { 1.0, double.NaN, -0.0 })
      calls.Add(Call("nan_equal", x));

    foreach (var (v, i) in new (byte, ulong)[] { (9, 0), (250, 3), (1, 4) })
      calls.Add(Call("grid", v, i));

    calls.Add(Call("field_order", new byte[] { 1, 2, 3 }, (byte)0));
    calls.Add(Call("field_order", new byte[] { 1 }, (byte)5));
    calls.Add(Call("field_order", new byte[] { 1, 2 }, (byte)5));
    foreach (var n in new uint[] { 0, 1, 2, 3, 4, 10, 11 })
      calls.Add(Call("classify", n));

    return calls;
  }

  private static IReadOnlyList<PrototypeCall> Edges() {
    var calls = new List<PrototypeCall>();
    foreach (var x in new uint[] { 0, 2, 4, 5 })
      calls.Add(Call("equalities", x));

    foreach (var x in new uint[] { 3, 5, 200 })
      calls.Add(Call("unit_results", x));

    foreach (var x in new uint[] { 3, 9 })
      calls.Add(Call("unwrap_unit", x));

    foreach (var i in new ulong[] { 0, 2, 3 })
      calls.Add(Call("defaults", i));

    foreach (var c in new[] { true, false })
      calls.Add(Call("chosen", c));

    foreach (var x in new uint[] { 3, uint.MaxValue })
      calls.Add(Call("pointers", x));

    foreach (var data in new double[][] { [1.5, 2.0], [], [double.NaN] })
      calls.Add(Call("float_folds", data));

    foreach (var n in new[] { 5, int.MaxValue })
      calls.Add(Call("vector_places", n));

    foreach (var n in new[] { 2, int.MaxValue })
      calls.Add(Call("temporaries", n));

    foreach (var i in new ulong[] { 0, 2, 3 })
      calls.Add(Call("struct_copies", i));

    foreach (var x in new uint[] { 4, uint.MaxValue })
      calls.Add(Call("closure_try", x));

    foreach (var i in new ulong[] { 0, 1, 2, 3 })
      calls.Add(Call("tables", i));

    foreach (var (x, y) in new (int, int)[] { (0, 0), (1, 5), (3, 3), (0, 9), (4, 2), (-1, 300) })
      calls.Add(Call("patterns", x, y));

    foreach (var n in new uint[] { 0, 3, 8, 20 })
      calls.Add(Call("switches", n));

    foreach (var n in new uint[] { 0, 1, 5 })
      calls.Add(Call("returning_switch", n));

    foreach (var high in new[] { true, false })
      calls.Add(Call("levels", high));

    foreach (var n in new uint[] { 1, uint.MaxValue - 1 })
      calls.Add(Call("into_iteration", n));

    foreach (var x in new[] { 3, 0, int.MaxValue })
      calls.Add(Call("struct_options", x));

    foreach (var x in new[] { 3, 11, int.MaxValue / 2 + 1 })
      calls.Add(Call("nested_return", x));

    calls.Add(Call("diverging_assignment", new byte[] { 1 }, true));
    calls.Add(Call("diverging_assignment", new byte[] { 1 }, false));
    calls.Add(Call("diverging_assignment", new byte[0], false));
    calls.Add(Call("diverging_assignment", new byte[] { 255 }, false));
    return calls;
  }

  private static IReadOnlyList<PrototypeCall> Closures() {
    var calls = new List<PrototypeCall>();
    foreach (var x in new uint[] { 5, 0, uint.MaxValue / 3 })
      calls.Add(Call("apply_twice", x));

    foreach (var x in new uint[] { 5, uint.MaxValue / 4 })
      calls.Add(Call("pointer_parameter", x));

    foreach (var n in new uint[] { 5, 0, 1, 2000 })
      calls.Add(Call("square_sum", n));

    foreach (var n in new uint[] { 5, 0, 12, 13 })
      calls.Add(Call("factorial", n));

    foreach (var data in new uint[][] { [1, 2, 3, 4, 5, 6], [], [65536] })
      calls.Add(Call("even_squares", data));

    foreach (var data in new uint[][] { [1, 2, 3, 4], [] })
      calls.Add(Call("poly_hash", data));

    foreach (var (a, b) in new (uint, uint)[] { (2, 5), (uint.MaxValue, 1) })
      calls.Add(Call("total", a, b));

    foreach (var (k, x) in new (uint, uint)[] { (3, 4), (uint.MaxValue, 1) })
      calls.Add(Call("offset", k, x));

    foreach (var z in new uint[] { 2, 0, 10 }) {
      calls.Add(Call("probe", new uint[] { 4, 0, 9 }, z));
      calls.Add(Call("counted_probe", new uint[] { 4, 0, 9 }, z));
    }

    foreach (var (data, limit) in new (int[], int)[] { ([1, 2, 3], 0), ([1, 2, 3], 1), ([], 5) })
      calls.Add(Call("all_above", data, limit));

    foreach (var data in new byte[][] { [1, 2, 3, 4, 5], [] })
      calls.Add(Call("count_odd", data));

    foreach (var data in new ushort[][] { [1, 2, 65535], [] })
      calls.Add(Call("for_each_total", data));

    foreach (var data in new uint[][] { [1, 2, 3, 4, 5, 6, 7], [], [uint.MaxValue] })
      calls.Add(Call("chained", data));

    foreach (var n in new uint[] { 0, 1, 7, 10 })
      calls.Add(Call("range_chain", n));

    foreach (var data in new byte[][] { [200, 100], [1, 2], [] })
      calls.Add(Call("byte_sum", data));

    foreach (var x in new[] { 5, 70, -3, 200, int.MaxValue })
      calls.Add(Call("early_exit", x));

    foreach (var x in new uint[] { 21, 14, 5, 1001, 0 })
      calls.Add(Call("checked", x));

    foreach (var x in new uint[] { 0, 1, 2, 3, 4 })
      calls.Add(Call("panics", x));

    foreach (var n in new uint[] { 0, 1, 10, 20 })
      calls.Add(Call("fib", n));

    foreach (var n in new uint[] { 0, 7, 10 })
      calls.Add(Call("mutual", n));

    foreach (var (a, b) in new (uint, uint)[] { (3, 4), (65536, 65536) })
      calls.Add(Call("pattern_parameters", a, b));

    return calls;
  }

}
