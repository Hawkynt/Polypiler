using System;
using System.Collections.Generic;
using System.Linq;
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

  private static readonly int[] _ints = [int.MinValue, -1, 0, 1, 31, 32, 33, int.MaxValue];
  private static readonly uint[] _uints = [0, 1, 0x80000000, uint.MaxValue];
  private static readonly byte[] _bytes = [0, 1, 0x80, 255];
  private static readonly bool[] _bools = [false, true];

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
    "Operators" => Operators(),
    _ => throw new ArgumentException($"There is no fixture '{fixture}'.", nameof(fixture))
  };

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
  /// For every function of the operator fixture: all combinations of the boundary values of its
  /// parameter types, then <see cref="RandomCallsPerOperator"/> calls with random operands.
  /// </summary>
  private static List<PrototypeCall> Operators() {
    var random = new Random(Seed);
    var calls = new List<PrototypeCall>();
    var methods = PrototypeFixture.Compiled("Operators", "AddInt").DeclaringType!
      .GetMethods(BindingFlags.Public | BindingFlags.Static)
      .OrderBy(method => method.Name, StringComparer.Ordinal);
    foreach (var method in methods) {
      var types = method.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
      IEnumerable<object[]> combinations = [[]];
      foreach (var type in types)
        combinations = combinations.SelectMany(_ => Boundaries(type), (prefix, value) => prefix.Append(value).ToArray()).ToList();

      calls.AddRange(combinations.Select(arguments => new PrototypeCall(method.Name, arguments)));
      for (var index = 0; index < RandomCallsPerOperator; ++index)
        calls.Add(new PrototypeCall(method.Name, types.Select(type => RandomOperand(type, random)).ToArray()));
    }

    return calls;
  }

  private static object[] Boundaries(Type type) {
    if (type == typeof(int))
      return _ints.Cast<object>().ToArray();

    if (type == typeof(uint))
      return _uints.Cast<object>().ToArray();

    return type == typeof(byte) ? _bytes.Cast<object>().ToArray() : _bools.Cast<object>().ToArray();
  }

  /// <summary>A random operand: one time in eight a boundary value, otherwise any value of the type.</summary>
  private static object RandomOperand(Type type, Random random) {
    var boundaries = Boundaries(type);
    if (random.Next(8) == 0)
      return boundaries[random.Next(boundaries.Length)];

    var bits = (uint)random.NextInt64(1L << 32);
    if (type == typeof(int))
      return (int)bits;

    if (type == typeof(uint))
      return bits;

    return type == typeof(byte) ? (byte)bits : (bits & 1) != 0;
  }

}
