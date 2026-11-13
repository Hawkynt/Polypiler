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
    [typeof(ulong)] = Integer<ulong>()
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
  /// For every public function of a fixture that takes only scalars: all combinations of the boundary
  /// values of its parameter types, then <see cref="RandomCallsPerOperator"/> calls with random operands.
  /// </summary>
  private static List<PrototypeCall> EveryMethod(string fixture) {
    var random = new Random(Seed);
    var calls = new List<PrototypeCall>();
    var methods = PrototypeFixture.CompiledType(fixture)
      .GetMethods(BindingFlags.Public | BindingFlags.Static)
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
