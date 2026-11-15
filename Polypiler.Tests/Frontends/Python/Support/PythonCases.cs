using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// The calls the imported Python fixtures are compared on. Nothing here says what a call returns: the
/// expected outcome is what CPython does with the original source (<see cref="PythonFixture"/>).
/// A function of <c>scalars</c> is called with every combination of the boundary values of its
/// parameter types, and with random operands; the functions of the other fixtures with lists of their own.
/// </summary>
internal static class PythonCases {

  /// <summary>The seed of the random operands.</summary>
  public const int Seed = 1800;

  /// <summary>How many random calls every function of two operands gets.</summary>
  public const int RandomCalls = 200;

  /// <summary>Integers around the boundaries of 31, 32, 53 and 63 bits, small ones and the signs.</summary>
  public static readonly long[] Ints = [
    long.MinValue, long.MinValue + 1, -(1L << 53) - 1, -4294967296, -2147483649, -2147483648, -7, -3, -2, -1, 0, 1, 2, 3, 7,
    2147483647, 2147483648, 4294967295, 4294967296, 1L << 53, (1L << 53) + 1, 1L << 62, long.MaxValue
  ];

  /// <summary>Integers of 32 bits, for the functions that mask to them.</summary>
  public static readonly long[] Words = [0, 1, 2, 97, 5381, 65535, 65536, 2147483647, 2147483648, 4294967294, 4294967295];

  /// <summary>Shift counts and exponents: negative, small, at the widths and beyond them.</summary>
  public static readonly long[] Counts = [-1, 0, 1, 2, 3, 10, 31, 32, 62, 63, 64, 70, 1000];

  /// <summary>Floats: both zeros, halves that round either way, decimals, the limits of 53 and 63 bits, the extremes, the infinities and NaN.</summary>
  public static readonly double[] Floats = [
    0.0, -0.0, 0.5, -0.5, 1.0, -1.0, 1.5, 2.5, -2.5, 3.5, -7.5, 0.1, 0.2, 9007199254740992.0, 9223372036854775808.0, -9223372036854775808.0,
    1e300, -1e300, 5e-324, double.PositiveInfinity, double.NegativeInfinity, double.NaN
  ];

  /// <summary>The functions of <c>scalars</c> called with other values than the boundaries of their types.</summary>
  private static readonly Dictionary<string, long[][]> _scalarArguments = new() {
    ["power"] = [Ints, [0, 1, 2, 3, 10, 31, 32, 62, 63, 64]],
    ["shift_left"] = [Ints, Counts],
    ["shift_right"] = [Ints, Counts],
    ["hash_step"] = [Words, Words],
    ["masked_sum"] = [Words, Words]
  };

  /// <summary>The calls of a fixture.</summary>
  public static IReadOnlyList<PythonCall> For(string fixture, IrModule module) => fixture switch {
    "scalars" => Scalars(module),
    "control" => Control,
    "memory" => Memory,
    "types" => Types,
    "modules" => Modules,
    _ => Functions
  };

  private static List<PythonCall> Scalars(IrModule module) {
    var calls = new List<PythonCall>();
    var random = new Random(Seed);
    foreach (var function in module.Functions.Where(function => function.IsPublic)) {
      IReadOnlyList<IReadOnlyList<object>> values = _scalarArguments.TryGetValue(function.Name, out var given)
        ? [.. given.Select(set => set.Cast<object>().ToList())]
        : [.. function.Parameters.Select(parameter => Values(parameter.Type).Take(function.Parameters.Count > 2 ? 8 : int.MaxValue).ToList())];
      foreach (var arguments in Product(values))
        calls.Add(new PythonCall(function.Name, [.. arguments]));

      if (function.Parameters.Count == 2 && !_scalarArguments.ContainsKey(function.Name))
        for (var index = 0; index < RandomCalls; ++index)
          calls.Add(new PythonCall(function.Name, [.. function.Parameters.Select(parameter => Random(random, parameter.Type))]));
    }

    return calls;
  }

  private static IReadOnlyList<object> Values(IrType type)
    => type == IrType.BigInt ? [.. Ints.Cast<object>()] : type == IrType.F64 ? [.. Floats.Cast<object>()] : [false, true];

  /// <summary>A random operand: an integer of a random width up to 63 bits with a random sign, a float of random bits, a truth value.</summary>
  private static object Random(Random random, IrType type) {
    if (type == IrType.Bool)
      return random.Next(2) == 1;

    if (type == IrType.F64) {
      var bits = random.NextInt64() ^ ((long)random.Next(2) << 63);
      var number = BitConverter.Int64BitsToDouble(bits);
      // Small numbers with few digits as well as numbers of random bits.
      return random.Next(2) == 0 ? number : Math.Round((random.NextDouble() - 0.5) * 2000, random.Next(4));
    }

    var width = random.Next(1, 64);
    var magnitude = random.NextInt64() >> (63 - width);
    return random.Next(2) == 0 ? magnitude : -magnitude;
  }

  private static IEnumerable<List<object>> Product(IReadOnlyList<IReadOnlyList<object>> sets) {
    IEnumerable<List<object>> result = [[]];
    foreach (var set in sets)
      result = result.SelectMany(prefix => set.Select(value => new List<object>(prefix) { value }));

    return result;
  }

  private static PythonCall Call(string function, params object[] arguments) => new(function, arguments);

  private static IEnumerable<PythonCall> Each(string function, params long[] values) => values.Select(value => Call(function, value));

  private static readonly IReadOnlyList<PythonCall> Control = [
    .. Floats.Select(value => Call("sign", value)),
    .. Each("truth_branch", -1, 0, 1),
    .. Each("sum_to", 0, 1, 10, 100),
    .. Each("digits_up", 0, 1, 4, 9),
    .. Each("digits_down", 0, 1, 4, 9),
    .. new long[] { 0, 1, 7, 10, -10 }.SelectMany(start => new long[] { 0, 3, 10, -3 }.SelectMany(stop => new long[] { 1, 2, 3, 10, -1, -3, 0 }.Select(step => Call("stepped", start, stop, step)))),
    Call("stepped", long.MaxValue - 10, long.MaxValue, 3L),
    Call("stepped", long.MinValue + 10, long.MinValue, -4L),
    Call("stepped", long.MinValue, long.MaxValue, long.MaxValue),
    Call("stepped", long.MaxValue, long.MinValue, long.MinValue),
    .. Each("last_value", 0, 1, 5),
    .. Each("range_once", 0, 3, 5),
    .. Each("is_prime", 0, 1, 2, 3, 4, 7, 9, 97, 100),
    .. new long[] { 1, 2, 10 }.SelectMany(n => new long[] { 1, 3, 20 }.Select(k => Call("first_multiple", n, k))),
    .. Each("collatz", 1, 2, 3, 7, 27),
    .. Each("while_else", -1, 0, 1, 7, 9, 10, 13),
    .. Each("while_true", 0, 1, 2, 10, 1000000),
    .. Each("nested", 0, 1, 5, 10),
    .. Each("classify", long.MinValue, -2, -1, 0, 1, 2, 3, 9, 10, long.MaxValue),
    .. Each("capture", -5, 0, 5, 100, 101, 1000),
    Call("match_bool", false), Call("match_bool", true),
    .. new[] { 0.0, 1.0, 2.5, 2.0, -1.0, double.NaN }.Select(value => Call("match_float", value)),
    .. Each("swap", 0, 1, 7).Select(call => call with { Arguments = [call.Arguments[0], 3L] }),
    Call("rotate", 1L, 2L, 3L), Call("rotate", 7L, 8L, 9L),
    .. Each("chained_assign", -3, 0, 21),
    .. Each("checked", -1, 0, 5),
    .. Each("trap_in_loop", 0, 1, 2, 3, 4),
    .. new long[] { 0, 12, 18, -12, 1071 }.SelectMany(a => new long[] { 0, 8, 462, -18 }.Select(b => Call("gcd", a, b)))
  ];

  private static IEnumerable<PythonCall> Pairs(string function, params (long, long)[] pairs) => pairs.Select(pair => Call(function, pair.Item1, pair.Item2));

  /// <summary>The calls of <c>memory</c> (#451): each sequence operation on its boundaries - indices on and beyond both ends, empty sequences, counts below one, values at the limits of their fields.</summary>
  private static readonly IReadOnlyList<PythonCall> Memory = [
    .. Each("index", -6, -5, -1, 0, 4, 5),
    .. Pairs("store", (-4, 1), (-3, 1), (-1, 7), (0, 5), (2, 9), (3, 1)),
    .. Each("augmented_store", -4, -1, 0, 1, 2, 3),
    .. new (long, long, long)[] { (0, 5, 1), (1, 3, 1), (-2, 10, 1), (10, 20, 1), (4, -6, -1), (0, 5, 2), (5, 0, -2), (0, 5, 0), (-100, 100, 3), (1, 1, 1), (3, 1, 1), (-1, -6, -2) }
      .Select(bounds => Call("slice_sum", bounds.Item1, bounds.Item2, bounds.Item3)),
    .. Each("slice_open", -1, 0, 2, 5, 7),
    .. Each("repeat", -1, 0, 1, 1000),
    Call("aliased_rows", false), Call("aliased_rows", true),
    .. Each("append_pop", 0, 1, 5),
    .. Each("pop_empty", 1),
    .. Each("extend_self", 0, 1, 3),
    .. Each("concat_copy", 4),
    .. Each("contains", 1, 2, 3, 7, 9),
    .. Pairs("list_equal", (1, 2), (2, 1), (3, 3), (1, 1)),
    .. Each("iterate_growing", 0, 1, 5),
    .. Each("enumerated", -3, 0, 1, long.MaxValue - 2, long.MaxValue - 1),
    .. Each("zipped", 0, 30),
    .. Each("reversed_loop", 0, 3, 4),
    Call("sorted_values", false), Call("sorted_values", true),
    .. Each("sorted_by_key", -3, 0, 2, 5),
    .. Each("folds", -10, 0, 1, 100),
    .. Each("min_empty", 0, 3),
    .. Pairs("max_args", (1, 2), (5, 4), (3, 3), (-1, -1)),
    .. Each("sum_start", -2, 0, 3, 10),
    .. Each("comprehension", 0, 1, 5, 6),
    .. Each("nested_comprehension", 0, 3, 5),
    .. Each("generator_any", 0, 5, 9),
    .. Each("list_of", 0, 1, 5),
    .. Each("bytes_index", -5, -4, -1, 0, 3, 4),
    .. Each("bytes_slice", 0, 2, 4),
    .. Each("bytearray_store", -1, 0, 255, 256),
    .. Each("bytes_of_list", -1, 0, 255, 256),
    .. Each("bytes_ops", 0, 2),
    Call("from_bytes", false), Call("from_bytes", true),
    .. Each("from_bytes_signed", 0, 1, 2, 8, 9, 10),
    .. Each("to_bytes", -32769, -32768, 0, 255, 32767, 32768),
    .. Each("to_bytes_unsigned", -1, 0, 4294967295, 4294967296),
    .. Each("struct_unpack", -1, 0, 65535, 65536),
    .. Each("struct_big", 0, 5, 32768, 32769),
    .. Each("struct_from", -5, -4, -3, -1, 0, 1, 2, 3),
    .. new[] { 0.5, -1.25, 1e300, double.NaN, double.PositiveInfinity, 3.4028234663852886e38 }.Select(value => Call("struct_floats", value)),
    .. Each("struct_short", 3, 4, 5),
    .. Each("struct_range", -1, 0, 255, 256),
    .. Each("adler32", 0, 1, 3),
    .. Pairs("tuples", (1, 2), (0, 0)),
    .. Pairs("swap_return", (1, 2), (7, 3)),
    .. Pairs("divmod_tuple", (7, 2), (-7, 2), (7, 0)),
    .. Each("strings", 0, 1, 3),
    .. Each("string_iteration", 0, 1, 2),
    .. Each("encode", 0, 1, 2),
    .. Each("encode_surrogate", 0, 1),
    .. Each("characters", -1, 0, 65, 233, 0xD800, 0x10FFFF, 0x110000),
    .. Each("string_in", 0, 1, 2, 3),
    .. new[] { 0.0, 2.0, double.NaN, double.NegativeInfinity }.Select(value => Call("float_list", value))
  ];

  private static IEnumerable<PythonCall> Flagged(string function, params (long, bool)[] pairs) => pairs.Select(pair => Call(function, pair.Item1, pair.Item2));

  /// <summary>The calls of <c>types</c> (#453): every class kind, enum, optional and exception form on the values that choose each branch.</summary>
  private static readonly IReadOnlyList<PythonCall> Types = [
    .. Pairs("point", (3, 4), (-2, 0), (0, 0)),
    .. Each("dataclass_equality", 1, 3),
    .. Each("plain_equality", 1),
    .. Each("frozen", -2, 1),
    .. Each("identity", 1),
    .. Each("counter", 0, 1, 4),
    .. Pairs("grid", (0, 5), (5, 255), (6, 1), (-1, 7), (2, 256)),
    .. Pairs("money", (1, 1), (1, 2)),
    .. Each("sharing", 0, 5),
    .. Pairs("named_tuple", (1, 2), (3, 3)),
    .. Each("enum_value", 0, 1, 2, 3, 4),
    .. Each("enum_compare", 1, 2),
    .. Each("enum_iteration", 0),
    .. Each("int_enum", 0, 1, 2, 3),
    .. Flagged("optional_value", (5, false), (5, true), (0, false)),
    .. Flagged("optional_truth", (0, false), (0, true), (7, false)),
    .. Flagged("optional_arithmetic", (1, false), (1, true)),
    .. Each("optional_object", 0, 3),
    .. Each("none_attribute", 0, 3),
    .. Each("maybe", -1, 0, 4),
    .. Each("raises", -1, 0, 5),
    .. Pairs("catches", (6, 3), (1, 0), (-1, 1)),
    .. Each("handlers", -4, 0, 1, 2, 3),
    .. Each("lookup", -3, 0, 2),
    .. Each("else_finally", 0, 1, 2),
    .. Each("user_errors", 0, 1, 2, 3),
    .. Each("reraise", 0, 2),
    .. Each("nested_try", 0, 1, 2, 3),
    .. Each("uncaught", 1, 2, 3, 4),
    .. Each("loop_finally", 0, 1, 2, 5),
    .. Each("bare_except", 0, 5),
    .. Each("raise_in_handler", 0, 5),
    .. Each("not_implemented", 0, 1),
    .. Each("instance_check", 0, 1),
    .. Each("raised_object", 0, 1)
  ];

  /// <summary>The calls of <c>modules</c> (#450): defaults and keywords, closures, lambdas, module state, the call map, walrus and generators.</summary>
  private static readonly IReadOnlyList<PythonCall> Modules = [
    .. Flagged("defaults", (5, true), (5, false)),
    .. Each("call_kinds", 0, 1),
    .. Each("order", 0),
    .. Pairs("closure", (10, 7), (10, 12)),
    .. Each("late_binding", 3),
    .. Each("counter_closure", 0, 4),
    .. Each("two_levels", 1),
    .. Each("recursive_nested", 0, 5, 20, 21),
    .. Each("square", -3, 3037000500),
    .. Each("lambdas", -4, 0, 5),
    .. Each("map_function", -3, 0, 4),
    .. Each("module_table", -7, -6, 0, 5, 6),
    .. Each("weights", 0, 2),
    .. Each("global_counter", 3),
    .. new[] { 0.0, -0.0, -2.25, double.NaN, double.PositiveInfinity, double.NegativeInfinity }.Select(value => Call("math_calls", value)),
    .. new[] { 2.5, -2.5, 0.5, -0.5, 1e300, double.NaN, double.PositiveInfinity }.Select(value => Call("math_ints", value)),
    .. new[] { 4.0, 2.0, -1.0, -0.0, double.NaN, double.PositiveInfinity }.Select(value => Call("math_sqrt", value)),
    .. Pairs("integer_math", (12, 18), (0, 0), (-12, 8), (long.MinValue, 0), (99, long.MaxValue)),
    .. Each("walrus", 0, 1, 5),
    .. Each("generators", 0, 1, 4),
    .. Each("generator_early", 3, 11),
    .. Each("imported", 0, 4)
  ];

  private static readonly IReadOnlyList<PythonCall> Functions = [
    .. new long[] { -3, 0, 2, 3037000499 }.SelectMany(a => new long[] { 0, 4, 3037000500 }.Select(b => Call("sum_of_squares", a, b))),
    Call("hypotenuse_squared", 3.0, 4.0), Call("hypotenuse_squared", 0.1, 0.2), Call("hypotenuse_squared", 1e200, 1e200),
    .. Each("halves", -3, 0, 1, 7, 1L << 53, long.MinValue),
    .. Each("factorial", -1, 0, 1, 5, 20, 21),
    .. Each("fibonacci", 0, 1, 2, 10, 50, 91, 93),
    .. Each("is_even", 0, 1, 10, 31),
    .. Each("is_odd", 0, 1, 10, 31),
    Call("ackermann", 0L, 0L), Call("ackermann", 1L, 2L), Call("ackermann", 2L, 3L), Call("ackermann", 3L, 3L),
    .. Each("count_down", 0, 3),
    .. Each("uses_none_function", 0, 5),
    Call("mean", 1.0, 2.0, 4.0), Call("mean", 0.1, 0.2, 0.3), Call("mean", double.PositiveInfinity, double.NegativeInfinity, 1.0),
    Call("adler32_step", 1L, 0L, 87L), Call("adler32_step", 65520L, 65520L, 255L)
  ];

}
