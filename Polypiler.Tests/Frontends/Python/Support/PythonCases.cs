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
    => type == IrType.I64 ? [.. Ints.Cast<object>()] : type == IrType.F64 ? [.. Floats.Cast<object>()] : [false, true];

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
