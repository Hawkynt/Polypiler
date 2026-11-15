using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// The calls the Rust fixtures are run with. For <c>scalars</c> they are generated from the
/// parameter types of every public function: the boundaries of each type (<c>MIN</c>, <c>MIN+1</c>,
/// -1, 0, 1, <c>MAX-1</c>, <c>MAX</c>), shift amounts around the width, the special floats (NaN, the
/// infinities, both zeros, the largest value, a subnormal, the precision edges 2^24+1 and 2^53+1),
/// and random operands from a fixed seed. The calls of <c>control</c> and <c>functions</c> are
/// written by hand, because their loops must terminate.
/// </summary>
internal static partial class RustCases {

  /// <summary>The seed of the random operands.</summary>
  public const int Seed = 1500;

  /// <summary>The random operand tuples each generated function is called with besides its boundaries.</summary>
  public const int RandomPerFunction = 12;

  /// <summary>The calls of a fixture, in the order of its recordings.</summary>
  public static IReadOnlyList<PrototypeCall> For(string fixture, IrModule module) => fixture switch {
    "scalars" => Generated(module),
    "control" => Control(),
    "memory" => Memory(),
    "types" => Types(),
    "closures" => Closures(),
    "edges" => Edges(),
    "strings" => Text(),
    _ => Functions()
  };

  // ---------------------------------------------------------------- generated

  private static IReadOnlyList<PrototypeCall> Generated(IrModule module) {
    var random = new Random(Seed);
    var calls = new List<PrototypeCall>();
    foreach (var function in module.Functions.Where(function => function.IsPublic)) {
      var types = function.Parameters.Select(parameter => parameter.Type).ToList();
      var lists = types.Select((type, index) => types.Count <= 1 ? Boundaries(type) : Few(type)).ToList();
      foreach (var arguments in Product(lists))
        calls.Add(new PrototypeCall(function.Name, arguments));

      // A shift, a rotation or a power: the amounts around the width of the first operand.
      if (types.Count == 2 && types[1] == IrType.U32 && types[0].IsInteger)
        foreach (var value in new[] { 1, 3, types[0].IsSigned ? -1 : IrOperators.Maximum(types[0]), types[0].IsSigned ? IrOperators.Minimum(types[0]) : 5 })
          foreach (var amount in Amounts(types[0].Bits))
            calls.Add(new PrototypeCall(function.Name, [Value(types[0], value), (uint)amount]));

      if (types.Count > 0)
        for (var index = 0; index < RandomPerFunction; ++index)
          calls.Add(new PrototypeCall(function.Name, [.. types.Select(type => Random(type, random))]));
    }

    return calls;
  }

  private static IEnumerable<object[]> Product(IReadOnlyList<IReadOnlyList<object>> lists) {
    IEnumerable<object[]> product = [[]];
    foreach (var list in lists)
      product = product.SelectMany(prefix => list.Select(value => (object[])[.. prefix, value]));

    return product;
  }

  private static long[] Amounts(int bits) => [0, 1, 2, bits - 1, bits, bits + 1, 32, 33, 63, 64, 65, uint.MaxValue];

  /// <summary>The boundaries of a type, for a function of one parameter.</summary>
  private static IReadOnlyList<object> Boundaries(IrType type) {
    if (type == IrType.Bool)
      return [false, true];

    if (type.IsFloat)
      return [.. _floats.Select(value => Value(type, value)).Distinct()];

    var minimum = IrOperators.Minimum(type);
    var maximum = IrOperators.Maximum(type);
    var values = new List<Int128> { minimum, minimum + 1, -2, -1, 0, 1, 2, 3, 7, type.Bits - 1, type.Bits, type.Bits + 1, 127, 128, 255, 256, 65535, 65536, maximum - 1, maximum };
    return [.. values.Where(value => value >= minimum && value <= maximum).Distinct().Select(value => Value(type, value))];
  }

  /// <summary>The boundaries of a type for a function of several parameters, which multiply.</summary>
  private static IReadOnlyList<object> Few(IrType type) {
    if (type == IrType.Bool)
      return [false, true];

    if (type.IsFloat)
      return [.. new[] { 0.0, -0.0, 1.0, -1.5, 0.1, 1e10, double.NaN, double.PositiveInfinity, double.NegativeInfinity }.Select(value => Value(type, value)).Distinct()];

    var minimum = IrOperators.Minimum(type);
    var maximum = IrOperators.Maximum(type);
    var values = new List<Int128> { minimum, -1, 0, 1, 2, 7, maximum };
    return [.. values.Where(value => value >= minimum && value <= maximum).Distinct().Select(value => Value(type, value))];
  }

  private static readonly double[] _floats = [
    0.0, -0.0, 1.0, -1.0, 0.5, 1.5, -1.5, 2.5, 0.1, 255.9, -128.5, 65536.5, 3e9, -3e9, 1e10, -1e10, 4294967296.0, 9.3e18, 1.9e19, -9.3e18,
    16777217.0, 9007199254740993.0, 1e40, double.MaxValue, -double.MaxValue, double.Epsilon, 2.2250738585072014E-308,
    double.NaN, double.PositiveInfinity, double.NegativeInfinity
  ];

  private static object Random(IrType type, Random random) {
    if (type == IrType.Bool)
      return random.Next(2) == 1;

    if (type.IsFloat) {
      var bits = random.NextInt64() ^ ((long)random.Next() << 33);
      return type == IrType.F32 ? (object)BitConverter.Int32BitsToSingle((int)bits) : BitConverter.Int64BitsToDouble(bits);
    }

    var pattern = (long)((ulong)random.NextInt64() ^ ((ulong)random.Next() << 40));
    return Value(type, IrOperators.Number(type, IrOperators.Wrap(type, pattern)));
  }

  /// <summary>A number as the .NET value the interpreter takes for a type.</summary>
  public static object Value(IrType type, Int128 value) => type.Kind switch {
    IrTypeKind.I8 => (sbyte)value,
    IrTypeKind.U8 => (byte)value,
    IrTypeKind.I16 => (short)value,
    IrTypeKind.U16 => (ushort)value,
    IrTypeKind.I32 => (int)value,
    IrTypeKind.U32 => (uint)value,
    IrTypeKind.I64 => (long)value,
    _ => (ulong)value
  };

  private static object Value(IrType type, double value) => type == IrType.F32 ? (object)(float)value : value;

  // ---------------------------------------------------------------- written by hand

  private static PrototypeCall Call(string method, params object[] arguments) => new(method, arguments);

  private static IReadOnlyList<PrototypeCall> Control() {
    var calls = new List<PrototypeCall> {
      Call("gcd", 48u, 18u), Call("gcd", 0u, 5u), Call("gcd", 5u, 0u), Call("gcd", 0u, 0u), Call("gcd", uint.MaxValue, 65535u),
      Call("collatz_steps", 27UL), Call("collatz_steps", 1UL), Call("collatz_steps", 97UL),
      Call("next_power_of_two", 100u), Call("next_power_of_two", 0u), Call("next_power_of_two", 1u), Call("next_power_of_two", 2147483648u),
      Call("sum_to", 100u), Call("sum_to", 0u), Call("sum_to", 100000u), Call("sum_to", 65535u),
      Call("odd_sum"),
      Call("early", -5), Call("early", 4), Call("early", 0), Call("early", int.MaxValue),
      Call("labelled_block", 11), Call("labelled_block", 6), Call("labelled_block", 1), Call("labelled_block", 10),
      Call("count_bytes"), Call("count_signed_bytes"), Call("stepped_top"), Call("step_then_rev"), Call("rev_then_step"),
      Call("step_zero", 0UL), Call("step_zero", 3UL), Call("step_zero", 1UL), Call("step_zero", 100UL), Call("step_zero", ulong.MaxValue),
      Call("first_factor_pair", 12u), Call("first_factor_pair", 11u), Call("first_factor_pair", 81u), Call("first_factor_pair", 1u),
      Call("bounds_once", 5u), Call("bounds_once", 0u), Call("shadowed_counter"),
      Call("guard_order", 7u), Call("guard_order", 3u), Call("guard_order", 1u),
      Call("nested_continue", 0u), Call("nested_continue", 4u), Call("nested_continue", 12u),
      Call("loop_with_continue", 10u), Call("loop_with_continue", 0u),
      Call("while_block_condition", 5u), Call("while_block_condition", 0u),
      Call("labelled_in_loop", 9u), Call("labelled_in_loop", 0u),
      Call("assign_order", 5u, 7u), Call("assign_order", uint.MaxValue, 3u), Call("assign_order", 3u, uint.MaxValue),
      Call("operand_order", 2, 9), Call("operand_order", int.MaxValue, 1),
      Call("infinite_or_return", 10u), Call("infinite_or_return", 0u), Call("infinite_or_return", 1000000u),
      Call("short_circuit_effect", 2u), Call("short_circuit_effect", 4u), Call("short_circuit_effect", 9u)
    };
    foreach (var name in new[] { "classify", "classify_statement", "guard_traps", "if_statement", "if_expression" })
      foreach (var x in new[] { int.MinValue, -5, -1, 0, 1, 2, 3, 7, 8, 10, 11, int.MaxValue })
        calls.Add(Call(name, x));

    foreach (var a in new byte[] { 0, 1, 5, 9, 10, 100, 199, 200, 201, 255 })
      foreach (var b in new[] { false, true })
        calls.Add(Call("match_tuple", a, b));

    foreach (var a in new[] { false, true })
      foreach (var b in new ushort[] { 0, 1, 7, 65535 })
        calls.Add(Call("match_bool", a, b));

    foreach (var x in new byte[] { 0, 9, 10, 19, 20, 99, 100, 255 })
      calls.Add(Call("match_ranges", x));

    foreach (var x in new[] { long.MinValue, -1L, 0L, 1L, 999L, 1000L, 1001L, long.MaxValue })
      calls.Add(Call("match_constant", x));

    // Every range form over the bound pairs of -3..=3 and the type limits, with the steps 0 to 4.
    sbyte[] bounds = [sbyte.MinValue, -3, -2, -1, 0, 1, 2, 3, sbyte.MaxValue];
    foreach (var a in bounds)
      foreach (var b in bounds) {
        foreach (var form in new[] { "range_exclusive", "range_inclusive", "range_exclusive_rev", "range_inclusive_rev" })
          calls.Add(Call(form, a, b));

        foreach (var step in new ulong[] { 0, 1, 2, 3, 4, 300 })
          foreach (var form in new[] { "range_exclusive_step", "range_inclusive_step", "range_exclusive_step_rev", "range_inclusive_step_rev", "range_exclusive_rev_step", "range_inclusive_rev_step" })
            calls.Add(Call(form, a, b, step));
      }

    foreach (var (a, b) in new (byte, byte)[] { (0, 255), (250, 255), (255, 255), (3, 3), (4, 3), (0, 0) })
      calls.Add(Call("range_u8_top", a, b));

    foreach (var (a, b, n) in new (ulong, ulong, ulong)[] { (0, ulong.MaxValue, 1), (ulong.MaxValue - 5, ulong.MaxValue, 2), (5, 3, 1), (0, 100, ulong.MaxValue), (ulong.MaxValue, ulong.MaxValue, 7) })
      calls.Add(Call("range_u64", a, b, n));

    return calls;
  }

  private static IReadOnlyList<PrototypeCall> Functions() {
    var calls = new List<PrototypeCall>();
    foreach (var (x, y, z) in new (byte, byte, byte)[] { (97, 98, 99), (0, 0, 0), (255, 255, 255) })
      calls.Add(Call("adler32_of_three", x, y, z));

    foreach (var n in new uint[] { 0, 1, 2, 10, 20 })
      calls.Add(Call("fib", n));

    foreach (var n in new uint[] { 0, 1, 2, 7, 100 }) {
      calls.Add(Call("is_even", n));
      calls.Add(Call("is_odd", n));
    }

    foreach (var (a, b) in new (long, long)[] { (3, 4), (-3, 4), (3037000499, 0), (3037000500, 0), (long.MinValue, 0) })
      calls.Add(Call("sum_of_squares", a, b));

    foreach (var (a, b, c, d) in new (int, int, int, int)[] { (10, 2, 9, 3), (1, 0, 1, 0), (1, 1, 1, 0), (int.MinValue, -1, 1, 0) })
      calls.Add(Call("argument_order", a, b, c, d));

    foreach (var n in new uint[] { 0, 1, 5 })
      calls.Add(Call("no_result", n));

    foreach (var x in new uint[] { 1, 2463534242, uint.MaxValue })
      calls.Add(Call("xorshift32", x));

    foreach (var (b, e, m) in new (ulong, uint, ulong)[] { (2, 10, 1000), (3, 200, 1000000007), (ulong.MaxValue, 65537, 18446744073709551557), (5, 0, 1), (7, 3, 0) })
      calls.Add(Call("power_mod", b, e, m));

    return calls;
  }

}
