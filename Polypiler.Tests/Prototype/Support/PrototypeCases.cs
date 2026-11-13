using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// The calls every implementation is compared on. Nothing here says what a call returns: the expected
/// outcome is whatever the fixture compiled by the C# compiler does.
/// </summary>
internal static class PrototypeCases {

  /// <summary>The seed of the random operands.</summary>
  public const int Seed = 2026;

  /// <summary>How many random calls every operator function gets.</summary>
  public const int RandomCallsPerOperator = 1000;

  private static readonly bool[] _bools = [false, true];

  /// <summary>
  /// The boundary values of floating point: both zeros, both infinities, NaN, the smallest subnormal,
  /// the largest finite number, the last integers a float and a double hold exactly and their
  /// neighbours, halves that round either way, and the numbers just inside and outside the range of
  /// every integer type.
  /// </summary>
  private static readonly double[] _doubles = [
    0.0, -0.0, double.PositiveInfinity, double.NegativeInfinity, double.NaN, double.Epsilon, float.Epsilon,
    double.MaxValue, -double.MaxValue, float.MaxValue,
    16777215, 16777216, 16777217, 9007199254740991, 9007199254740992, 9007199254740993,
    0.5, -0.5, 1.5, 2.5, -2.5, 1, -1, 3,
    127.5, 128, -128.5, -129, 255.5, 256, 32767.5, 32768, -32769, 65535.5, 65536,
    2147483647, 2147483647.5, 2147483648, -2147483648.5, -2147483649, 4294967295.5, 4294967296,
    9223372036854774784.0, 9223372036854775808.0, -9223372036854775808.0, -9223372036854777856.0,
    18446744073709549568.0, 18446744073709551616.0, 1e30, -1e30
  ];

  /// <summary>
  /// For every parameter type: its boundary values, and how 64 random bits become one of its values.
  /// An <c>int</c> is also a shift count, so it has the counts around the widths 32 and 64 as well.
  /// </summary>
  private static readonly Dictionary<Type, (object[] Edges, Func<ulong, object> FromBits)> _operands = new() {
    [typeof(bool)] = ([false, true], bits => (bits & 1) != 0),
    [typeof(sbyte)] = Integer<sbyte>(),
    [typeof(byte)] = Integer<byte>(),
    [typeof(short)] = Integer<short>(),
    [typeof(ushort)] = Integer<ushort>(),
    [typeof(char)] = Integer<char>(),
    [typeof(int)] = Integer(31, 32, 33, 63, 64, 65),
    [typeof(uint)] = Integer<uint>(),
    [typeof(long)] = Integer<long>(),
    [typeof(ulong)] = Integer<ulong>(),
    [typeof(float)] = (_doubles.Select(value => (object)(float)value).Distinct().ToArray(), bits => (float)RandomDouble(bits, true)),
    [typeof(double)] = (_doubles.Cast<object>().ToArray(), bits => RandomDouble(bits, false))
  };

  /// <summary>The calls for a fixture.</summary>
  public static IReadOnlyList<PrototypeCall> For(string fixture) => fixture switch {
    "Adler32" => [
      Call("Compute", Text("Wikipedia")),
      Call("Compute", Text("")),
      Call("Compute", new byte[] { 0 }),
      Call("Compute", new byte[] { 255 }),
      Call("Compute", Enumerable.Repeat((byte)0xFF, 6000).ToArray())
    ],
    "Crc32" => [
      Call("Compute", Text("123456789")),
      Call("Compute", Text("")),
      Call("Compute", new byte[] { 0 }),
      Call("Compute", new byte[] { 255 })
    ],
    "Fnv1a" => [
      Call("Hash", Text("")),
      Call("Hash", Text("a")),
      Call("Hash", Text("foobar"))
    ],
    "Numbers" => Numbers(),
    "Arrays" => Arrays(),
    "Statements" => Statements(),
    "Operators" or "wide/OperatorsWide" or "wide/Conversions" => EveryMethod(fixture),
    "wide/RunLength" => RunLength(),
    "wide/DigitSum" => DigitSum(),
    "wide/BinarySearch" => BinarySearch(),
    "wide/BubbleSort" => BubbleSort(),
    "wide/StatementsWide" => StatementsWide(),
    "wide/Fnv1a64" => [
      Call("Hash", Text("")),
      Call("Hash", Text("a")),
      Call("Hash", Text("foobar")),
      Call("Hash", new byte[] { 0, 255 })
    ],
    "wide/SplitMix64" => [
      Call("First", 0UL),
      Call("First", 1UL),
      Call("First", ulong.MaxValue),
      Call("Nth", 0UL, 0),
      Call("Nth", 0UL, 1),
      Call("Nth", 0UL, 2),
      Call("Nth", 0x123456789ABCDEFUL, 1000)
    ],
    "wide/Xorshift64" => [
      Call("Next", 1UL),
      Call("Next", ulong.MaxValue),
      Call("NextStar", 1UL),
      Call("NextStar", 0x8000000000000000UL),
      Call("NextSigned", 1L),
      Call("NextSigned", -1L),
      Call("NextSigned", long.MinValue),
      Call("Skip", 1UL, 0),
      Call("Skip", 1UL, 1),
      Call("Skip", 88172645463325252UL, 100)
    ],
    "wide/Crc16" => [
      Call("Compute", Text("123456789")),
      Call("Compute", Text("")),
      Call("Compute", new byte[] { 0 }),
      Call("Compute", new byte[] { 255 })
    ],
    "wide/Horner" => Horner(),
    "wide/Fir" => Fir(),
    "wide/Fletcher16" => [
      Call("Compute", Text("abcde")),
      Call("Compute", Text("")),
      Call("Compute", new byte[] { 255 }),
      Call("Compute", Enumerable.Repeat((byte)0xFF, 600).ToArray())
    ],
    _ => throw new ArgumentException($"There is no fixture '{fixture}'.", nameof(fixture))
  };

  /// <summary>The boundary values of a parameter type.</summary>
  public static object[] Edges(Type type) => _operands[type].Edges;

  /// <summary>The bytes of an ASCII text.</summary>
  public static byte[] Text(string text) => Encoding.ASCII.GetBytes(text);

  private static PrototypeCall Call(string method, params object[] arguments) => new(method, arguments);

  private static List<PrototypeCall> RunLength() {
    var calls = new List<PrototypeCall>();
    byte[][] streams = [
      [],
      [0x00, 7],
      [0x02, 1, 2, 3, 0x80, 9, 0xFF, 5],
      [0xFE, 1],
      [0x05, 1, 2],
      [0x80, 4, 0x81],
      [0xFF],
      [0x7F]
    ];
    foreach (var stream in streams) {
      calls.Add(Call("Decode", stream, new byte[200]));
      calls.Add(Call("DecodedLength", stream));
    }

    // An output that is too short: the write outside it traps.
    calls.Add(Call("Decode", new byte[] { 0x02, 1, 2, 3 }, new byte[2]));
    calls.Add(Call("Decode", new byte[] { 0x80, 9 }, new byte[2]));
    calls.Add(Call("Decode", new byte[] { 0x00, 7 }, Array.Empty<byte>()));
    return calls;
  }

  private static List<PrototypeCall> DigitSum() {
    var calls = new List<PrototypeCall>();
    foreach (var number in new uint[] { 0, 9, 10, 99999, 123456, uint.MaxValue }) {
      calls.Add(Call("Sum", number));
      calls.Add(Call("Root", number));
    }

    foreach (var number in new ulong[] { 0, 9, 10, 99, 100, ulong.MaxValue })
      calls.Add(Call("Count", number));

    foreach (var number in new[] { 0, 2468, 13579, -2468, 1234567890123456789, long.MaxValue, long.MinValue })
      calls.Add(Call("SumOfEven", number));

    return calls;
  }

  private static List<PrototypeCall> BinarySearch() {
    var calls = new List<PrototypeCall>();
    int[][] arrays = [[], [1], [1, 3], [1, 3, 5, 7, 9], [int.MinValue, -1, 0, int.MaxValue]];
    foreach (var sorted in arrays)
      foreach (var key in new[] { int.MinValue, -1, 0, 1, 2, 3, 5, 9, 10, int.MaxValue }) {
        calls.Add(Call("Find", sorted, key));
        calls.Add(Call("LowerBound", sorted.Select(value => (long)value << 31).ToArray(), (long)key << 31));
        calls.Add(Call("FindDouble", sorted.Select(value => value / 2.0).ToArray(), key / 2.0));
      }

    calls.Add(Call("FindDouble", new[] { 0.5, 1.5 }, double.NaN));
    calls.Add(Call("FindDouble", new[] { -0.0, 1.5 }, 0.0));
    return calls;
  }

  private static List<PrototypeCall> BubbleSort() {
    var calls = new List<PrototypeCall>();
    short[][] arrays = [[], [1], [2, 1], [1, 2], [3, 1, 2, -5, short.MaxValue, short.MinValue], [1, 2, 3, 4, 5], [5, 4, 3, 2, 1], [7, 7, 7]];
    foreach (var data in arrays) {
      calls.Add(Call("Sort", data));
      calls.Add(Call("Swaps", data));
      calls.Add(Call("IsSorted", data));
    }

    return calls;
  }

  /// <summary>
  /// The statement fixture: the functions with loops get the counts that run them zero times, once
  /// and often and that break in the first and in the last pass; the others, which take scalars and
  /// have no loop, get boundary combinations and random operands like the operators.
  /// </summary>
  private static List<PrototypeCall> StatementsWide() {
    string[] loops = ["SumOfOdd", "TwoCounters", "BreakAt", "Nested", "DoOnce", "DoContinue", "SwitchInLoop", "StepInCondition"];
    var calls = EveryMethod("wide/StatementsWide", method => !loops.Contains(method.Name));
    foreach (var count in new[] { -1, 0, 1, 2, 3, 4, 5, 9, 10, 20 }) {
      calls.Add(Call("SumOfOdd", count));
      calls.Add(Call("TwoCounters", count));
      calls.Add(Call("DoOnce", count));
      calls.Add(Call("DoContinue", count));
      calls.Add(Call("SwitchInLoop", count));
      calls.Add(Call("StepInCondition", count));
      calls.Add(Call("StepThroughArray", new int[3], count));
      foreach (var other in new[] { -1, 0, 1, 2, 4, 5, 9 }) {
        calls.Add(Call("BreakAt", count, other));
        calls.Add(Call("Nested", count, other));
      }
    }

    calls.Add(Call("StepThroughArray", Array.Empty<int>(), 0));
    calls.Add(Call("StepThroughArray", Array.Empty<int>(), 1));
    foreach (var text in new[] { "", "education", "rhythm", "y", "jazz", "aeiou" })
      calls.Add(Call("Vowels", text.ToCharArray()));

    long[][] longs = [[], [1], [long.MaxValue, 1], [-5, 5, 7]];
    foreach (var values in longs)
      calls.Add(Call("SumEach", values));

    int[][] ints = [[], [1, 2], [257, -1, 3], [255], [256]];
    foreach (var values in ints) {
      calls.Add(Call("SumEachNarrowed", values));
      calls.Add(Call("SumEachChecked", values));
      calls.Add(Call("SumOfCall", values));
    }

    calls.Add(Call("SumEachDouble", new[] { 0.1f, 0.2f, 16777216f }));
    calls.Add(Call("SumEachDouble", Array.Empty<float>()));
    sbyte[][] signed = [[], [1, 2], [1, -1], [sbyte.MinValue], [0, 0, 0, -3]];
    foreach (var values in signed)
      calls.Add(Call("FirstNegative", values));

    ushort[][] units = [[], [0], [1, 2, 0, 3], [1, 2], [ushort.MaxValue]];
    foreach (var values in units)
      calls.Add(Call("CountUntilZero", values));

    // Index 0 inside, index 1 outside, where the step traps before it changes anything.
    foreach (var index in new[] { 0, 1, -1 }) {
      calls.Add(Call("StepElement", new[] { 5 }, index));
      calls.Add(Call("StepElement", new[] { int.MaxValue }, index));
      calls.Add(Call("StepLongElement", new[] { long.MinValue }, index));
      calls.Add(Call("StepByteElement", new byte[] { 0 }, index));
      calls.Add(Call("StepByteElement", new byte[] { 255 }, index));
      calls.Add(Call("StepFloatElement", new[] { 16777216f }, index));
      calls.Add(Call("StepFloatElement", new[] { 0.5f }, index));
      calls.Add(Call("StepCheckedElement", new sbyte[] { 5 }, index));
      calls.Add(Call("StepCheckedElement", new sbyte[] { 126 }, index));
      calls.Add(Call("StepCheckedElement", new sbyte[] { 127 }, index));
      calls.Add(Call("StepCheckedULongElement", new ulong[] { 1 }, index));
      calls.Add(Call("StepCheckedULongElement", new ulong[] { 0 }, index));
    }

    calls.Add(Call("CopyBackwards", new[] { 1, 2, 3 }, new int[3]));
    calls.Add(Call("CopyBackwards", Array.Empty<int>(), Array.Empty<int>()));
    calls.Add(Call("CopyBackwards", new[] { 1, 2 }, new int[1]));
    calls.Add(Call("ChooseArray", true, new long[] { 1 }, new long[] { 2 }));
    calls.Add(Call("ChooseArray", false, new long[] { 1 }, new long[] { 2 }));
    return calls;
  }

  private static List<PrototypeCall> Horner() {
    var calls = new List<PrototypeCall>();
    double[][] polynomials = [[], [7], [1, 2, 3], [0.1, -0.2, 0.3, -0.4, 0.5], [1e300, 1e300], [double.NaN, 1]];
    foreach (var coefficients in polynomials)
      foreach (var x in new[] { 0.0, -0.0, 1, -1, 0.1, 16777217, 1e200, double.PositiveInfinity, double.NaN }) {
        var singles = coefficients.Select(coefficient => (float)coefficient).ToArray();
        calls.Add(Call("Evaluate", coefficients, x));
        calls.Add(Call("EvaluateSingle", singles, (float)x));
        calls.Add(Call("EvaluateMixed", singles, x));
      }

    return calls;
  }

  private static List<PrototypeCall> Fir() {
    var calls = new List<PrototypeCall>();
    double[][] signals = [[], [1], [1, 2], [1, 0, 0, 0, 0], [0.1, 0.2, 0.3, 0.4, 0.5, 0.6], [16777217, 3, -16777217, 1e30, -1e30]];
    foreach (var signal in signals) {
      var singles = signal.Select(sample => (float)sample).ToArray();
      calls.Add(Call("Filter", signal, new double[signal.Length], 0.25, 0.5, 0.25));
      calls.Add(Call("Filter", signal, new double[signal.Length], 1.0 / 3.0, -1.0 / 3.0, 1e-7));
      calls.Add(Call("FilterSingle", singles, new float[signal.Length], 0.25f, 0.5f, 0.25f));
      calls.Add(Call("FilterSingle", singles, new float[signal.Length], 1f / 3f, -1f / 3f, 1e-7f));
      calls.Add(Call("MovingAverage", singles));
      calls.Add(Call("Energy", signal));
    }

    // An output that is too short: the write outside it traps after the elements before it were written.
    calls.Add(Call("Filter", new double[] { 1, 2, 3 }, new double[2], 1.0, 1.0, 1.0));
    return calls;
  }

  private static List<PrototypeCall> Numbers() {
    var calls = new List<PrototypeCall> {
      Call("Gcd", 1071, 462),
      Call("Gcd", 0, 0),
      Call("Gcd", 0, 5),
      Call("Gcd", 5, 0),
      Call("Gcd", -12, 18),
      Call("Gcd", int.MaxValue, 1),
      Call("Gcd", int.MinValue, -1)
    };
    foreach (var n in new[] { -1, 0, 1, 2, 47, 48 })
      calls.Add(Call("Fibonacci", n));

    foreach (var count in new[] { -1, 0, 1, 31, 32, 33 })
      calls.Add(Call("RotateLeft", 0x80000001u, count));

    return calls;
  }

  private static List<PrototypeCall> Arrays() {
    var calls = new List<PrototypeCall>();
    foreach (var length in new[] { 0, 1, 2, 4, 5 })
      calls.Add(Call("Reverse", Enumerable.Range(1, length).ToArray()));

    // Empty, single element and four elements; index 0 and Length-1 inside, -1 and Length outside.
    foreach (var length in new[] { 0, 1, 4 }) {
      var ints = Enumerable.Range(0, length).Select(index => int.MinValue + index).ToArray();
      var uints = Enumerable.Range(0, length).Select(index => uint.MaxValue - (uint)index).ToArray();
      var bytes = Enumerable.Range(0, length).Select(index => (byte)(255 - index)).ToArray();
      foreach (var index in new[] { -1, 0, length - 1, length }.Distinct()) {
        calls.Add(Call("ReadInt", ints, index));
        calls.Add(Call("ReadUInt", uints, index));
        calls.Add(Call("ReadByte", bytes, index));
        calls.Add(Call("HalfOf", uints, index));
        calls.Add(Call("IsLarge", uints, index));
        calls.Add(Call("ReadAtUnsignedIndex", ints, (uint)index));
        calls.Add(Call("WriteInt", ints, index, -7));
        calls.Add(Call("WriteUInt", uints, index, 7u));
        calls.Add(Call("WriteByte", bytes, index, (byte)7));
        calls.Add(Call("WriteTruncated", bytes, index, 0x1FF));
        calls.Add(Call("WriteTruncated", bytes, index, -1));
      }

      calls.Add(Call("LengthOfInts", ints));
      calls.Add(Call("LengthOfUInts", uints));
      calls.Add(Call("LengthOfBytes", bytes));
      calls.Add(Call("Sum", ints));
    }

    foreach (var length in new[] { -1, 0, 1, 4 }) {
      calls.Add(Call("CountUp", length));
      calls.Add(Call("Powers", length));
      calls.Add(Call("Ramp", length));
    }

    calls.Add(Call("Sum", new[] { int.MaxValue, 1 }));
    calls.Add(Call("Accumulate", new[] { 5, 6, 7, 8 }, new[] { 0 }));
    calls.Add(Call("Accumulate", new[] { 5, 6, 7, 8 }, new[] { 2 }));
    calls.Add(Call("Accumulate", new[] { 5, 6, 7, 8 }, new[] { 3 }));
    calls.Add(Call("Scramble", new byte[] { 1, 2, 3 }, new uint[] { 1, 2, 3 }));
    calls.Add(Call("Scramble", Array.Empty<byte>(), Array.Empty<uint>()));
    calls.Add(Call("Scramble", new byte[] { 1, 2 }, new uint[] { 1 }));
    return calls;
  }

  private static List<PrototypeCall> Statements() {
    var calls = new List<PrototypeCall>();
    foreach (var value in new[] { int.MinValue, -5, 0, 5, int.MaxValue }) {
      calls.Add(Call("Sign", value));
      calls.Add(Call("Clamp", value, -3, 3));
      calls.Add(Call("Declarations", value));
    }

    // Loops that run zero times, once and many times.
    foreach (var count in new[] { -3, 0, 1, 2, 10, 65536 }) {
      calls.Add(Call("SumTo", count));
      calls.Add(Call("Halvings", count));
      calls.Add(Call("IntegerRoot", count));
    }

    foreach (var size in new[] { 0, 1, 5 })
      calls.Add(Call("Triangle", size));

    foreach (var n in new[] { 0, 1, 5, 13, 20 })
      calls.Add(Call("Factorial", n));

    foreach (var n in new[] { 0, 1, 7, 10 })
      calls.Add(Call("IsEven", n));

    calls.Add(Call("Declarations", 46341));
    calls.Add(Call("IndexOf", Array.Empty<byte>(), (byte)1));
    calls.Add(Call("IndexOf", new byte[] { 1 }, (byte)1));
    calls.Add(Call("IndexOf", new byte[] { 3, 2, 1 }, (byte)1));
    calls.Add(Call("IndexOf", new byte[] { 3, 2, 1 }, (byte)9));
    foreach (var first in _bools) {
      calls.Add(Call("Both", first, new[] { 0 }));
      calls.Add(Call("Either", first, new[] { 0 }));
      foreach (var second in _bools)
        calls.Add(Call("Neither", first, second));
    }

    calls.Add(Call("Order", new[] { 0 }));
    calls.Add(Call("Touch", new[] { 0, 0 }));
    calls.Add(Call("Touch", new[] { 0 }));
    calls.Add(Call("Fill", Array.Empty<int>(), 7));
    calls.Add(Call("Fill", new[] { 0, 0, 0 }, -1));
    return calls;
  }

  /// <summary>
  /// For every public function of a fixture that takes only scalars and is wanted: all combinations of the boundary
  /// values of its parameter types, then <see cref="RandomCallsPerOperator"/> calls with random operands.
  /// </summary>
  private static List<PrototypeCall> EveryMethod(string fixture, Func<MethodInfo, bool>? wanted = null) {
    var random = new Random(Seed);
    var calls = new List<PrototypeCall>();
    var methods = PrototypeFixture.CompiledType(fixture)
      .GetMethods(BindingFlags.Public | BindingFlags.Static)
      .Where(method => method.GetParameters().All(parameter => _operands.ContainsKey(parameter.ParameterType)))
      .Where(method => wanted is null || wanted(method))
      .OrderBy(method => method.Name, StringComparer.Ordinal);
    foreach (var method in methods) {
      var types = method.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
      IEnumerable<object[]> combinations = [[]];
      foreach (var type in types)
        combinations = combinations.SelectMany(_ => _operands[type].Edges, (prefix, value) => prefix.Append(value).ToArray()).ToList();

      calls.AddRange(combinations.Select(arguments => new PrototypeCall(method.Name, arguments)));
      for (var index = 0; index < RandomCallsPerOperator; ++index)
        calls.Add(new PrototypeCall(method.Name, types.Select(type => RandomOperand(type, random)).ToArray()));
    }

    return calls;
  }

  /// <summary>
  /// The boundary values of an integer type: its smallest and largest value and their neighbours, the
  /// values around zero and, for an unsigned type, around the highest bit.
  /// </summary>
  private static (object[] Edges, Func<ulong, object> FromBits) Integer<T>() where T : IBinaryInteger<T>, IMinMaxValue<T> {
    var half = T.MaxValue >> 1;
    T[] edges = [T.MinValue, T.MinValue + T.One, T.Zero - T.One, T.Zero, T.One, half, half + T.One, T.MaxValue - T.One, T.MaxValue];
    return (edges.Distinct().Cast<object>().ToArray(), bits => T.CreateTruncating(bits));
  }

  private static (object[] Edges, Func<ulong, object> FromBits) Integer(params int[] counts) {
    var (edges, fromBits) = Integer<int>();
    return (edges.Concat(counts.Cast<object>()).ToArray(), fromBits);
  }

  /// <summary>
  /// 64 random bits as a floating-point number: a third of the time the bits themselves, which reaches
  /// subnormals, infinities and NaNs; a third an integer of random magnitude; a third a multiple of one
  /// half, where rounding and truncation differ. For a float the bits are 32.
  /// </summary>
  private static double RandomDouble(ulong bits, bool single) => (bits % 3) switch {
    0 => single ? BitConverter.Int32BitsToSingle((int)(bits >> 8)) : BitConverter.Int64BitsToDouble((long)bits),
    1 => (long)bits >> (int)((bits >> 8) & 63),
    _ => ((long)bits >> 44) / 2.0
  };

  /// <summary>
  /// A random operand: one time in eight a boundary value, one time in eight a value close to zero,
  /// otherwise any value of the type.
  /// </summary>
  private static object RandomOperand(Type type, Random random) {
    var (edges, fromBits) = _operands[type];
    return random.Next(8) switch {
      0 => edges[random.Next(edges.Length)],
      1 => fromBits((ulong)(random.Next(601) - 300)),
      _ => fromBits((ulong)random.NextInt64(long.MinValue, long.MaxValue))
    };
  }

}
