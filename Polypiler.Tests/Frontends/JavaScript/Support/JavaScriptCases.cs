using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Prototype.Testing;

/// <summary>A call on which JavaScript and the imported code differ on purpose.</summary>
/// <param name="Caveat">The caveat of the import that announces the difference.</param>
/// <param name="Fixture">The fixture the function is in.</param>
/// <param name="Call">The call.</param>
/// <param name="Node">What node did with the original source.</param>
/// <param name="Imported">What the interpreter and the code emitted from the IR do.</param>
internal sealed record JavaScriptDifference(string Caveat, string Fixture, PrototypeCall Call, string Node, string Imported) {

  /// <summary>The difference as a test name shows it.</summary>
  public override string ToString() => $"{this.Fixture}: {this.Call}";

}

/// <summary>
/// The calls the imported JavaScript is compared on. Nothing here says what a call returns: the
/// expected outcome is whatever <c>node</c> does with the original source.
/// <para>
/// No call of a fixture's list leaves an array, divides by zero or steps a variable out of its 32
/// bits. There JavaScript and the imported code differ on purpose, and <see cref="Differences"/>
/// states both sides.
/// </para>
/// </summary>
internal static class JavaScriptCases {

  /// <summary>The seed of the random operands.</summary>
  public const int Seed = 2026;

  /// <summary>How many random calls every operator function gets.</summary>
  public const int RandomCallsPerOperator = 1000;

  private static readonly int[] _ints = [int.MinValue, -1, 0, 1, 31, 32, 33, int.MaxValue];
  private static readonly uint[] _uints = [0, 1, 0x80000000, uint.MaxValue];
  private static readonly byte[] _bytes = [0, 1, 0x80, 255];
  private static readonly bool[] _bools = [false, true];

  /// <summary>
  /// The boundary values of a double: both zeros, the halves that round either way, the limits of 32
  /// bits and their neighbours, the last integers a double holds exactly, 1e21, the infinities and NaN.
  /// </summary>
  private static readonly double[] _doubles = [
    0.0, -0.0, 0.5, -0.5, 1, -1, 1.5, -1.5, 2.5, -2.5, 2147483647, 2147483648, -2147483649, 4294967296, 4294967297,
    9007199254740992, 9007199254740994, 1e21, double.PositiveInfinity, double.NegativeInfinity, double.NaN
  ];

  /// <summary>The calls for a fixture whose expected outcomes are recorded in the repository.</summary>
  public static IReadOnlyList<PrototypeCall> For(string fixture) => fixture switch {
    "adler32" => [
      Call("compute", PrototypeCases.Text("Wikipedia")),
      Call("compute", PrototypeCases.Text("")),
      Call("compute", new byte[] { 0 }),
      Call("compute", new byte[] { 255 }),
      Call("compute", Enumerable.Repeat((byte)0xFF, 600).ToArray())
    ],
    "crc32" => [
      Call("compute", PrototypeCases.Text("123456789")),
      Call("compute", PrototypeCases.Text("")),
      Call("compute", new byte[] { 0 }),
      Call("compute", new byte[] { 255 })
    ],
    "fnv1a" => [
      Call("hash", PrototypeCases.Text("")),
      Call("hash", PrototypeCases.Text("a")),
      Call("hash", PrototypeCases.Text("foobar"))
    ],
    "gcd" => [
      Call("gcd", 1071, 462),
      Call("gcd", 0, 0),
      Call("gcd", 0, 5),
      Call("gcd", 5, 0),
      Call("gcd", -12, 18),
      Call("gcd", 12, -18),
      Call("gcd", int.MaxValue, 1),
      Call("gcd", int.MinValue, int.MaxValue)
    ],
    "rotate" => Rotate(),
    "arrays" => Arrays(),
    "statements" => Statements(),
    "operators" => Operators(false),
    "untyped/adler32" => [
      Call("compute", PrototypeCases.Text("Wikipedia")),
      Call("compute", PrototypeCases.Text("")),
      Call("compute", new byte[] { 0 }),
      Call("compute", new byte[] { 255 }),
      Call("compute", Enumerable.Repeat((byte)0xFF, 600).ToArray()),
      Call("check")
    ],
    "untyped/crc32" or "untyped/crc32table" => [
      Call("compute", PrototypeCases.Text("123456789")),
      Call("compute", PrototypeCases.Text("")),
      Call("compute", new byte[] { 0 }),
      Call("compute", new byte[] { 255 }),
      Call("check")
    ],
    "untyped/fnv1a" or "untyped/fnv1a64" => [
      Call("hash", PrototypeCases.Text("")),
      Call("hash", PrototypeCases.Text("a")),
      Call("hash", PrototypeCases.Text("foobar")),
      Call("check")
    ],
    "untyped/xorshift32" => [
      .. new[] { 1, -1, 0, int.MaxValue, int.MinValue, -1831433054 }.Select(state => Call("next", state)),
      .. new[] { 0.0, 1.0, 2.5, 100.0, -3.0, double.NaN }.Select(count => Call("skip", 1, count)),
      Call("skip", int.MinValue, 7.0)
    ],
    "untyped/stats" => [
      .. new[] { new[] { 2.0, 4, 4, 4, 5, 5, 7, 9 }, [], [0.1], [0.1, 0.2, 0.3], [1e308, 1e308], [-0.0, 0.0], [double.NaN, 1] }
        .SelectMany(values => new[] { Call("mean", values), Call("variance", values) }),
      Call("check")
    ],
    "untyped/horner" => [
      Call("evaluate", new[] { 2.0, -6, 2, -1 }, 3.0),
      Call("evaluate", Array.Empty<double>(), 3.0),
      Call("evaluate", new[] { 0.1, 0.2, 0.3 }, 0.5),
      Call("evaluate", new[] { 1.0, 1, 1 }, double.PositiveInfinity),
      Call("evaluate", new[] { 1.0, 1, 1 }, double.NaN),
      Call("evaluate", new[] { 1.0, 0 }, -0.0),
      Call("check")
    ],
    "untyped/runlength" => [
      .. new[] { 0.0, 1.0, 2.0, 3.0, 0.5, double.NaN }.SelectMany(kind => new[] {
        Call("runs", new byte[] { 49, 50, 0, 0, 97, 55 }, kind),
        Call("runs", Array.Empty<byte>(), kind),
        Call("runs", new byte[] { 47, 48, 57, 58, 0, 48 }, kind)
      }),
      Call("check")
    ],
    "untyped/binarysearch" => [
      .. new[] { -8.0, -7.0, -1.0, 0.0, -0.0, 3.0, 3.5, 8.0, 21.0, 22.0, double.NaN }.Select(target => Call("find", new[] { -7, -1, 0, 3, 8, 21 }, target)),
      Call("find", Array.Empty<int>(), 1.0),
      Call("find", new[] { 5 }, 5.0),
      Call("check")
    ],
    "inference/arrays" => [
      .. BoundaryCalls(Scalar(fixture)),
      .. new[] { Array.Empty<sbyte>(), [-128, 127, -1] }.Select(items => Call("sumInt8", items)),
      .. new[] { Array.Empty<ushort>(), [65535, 1] }.Select(items => Call("sumUint16", items)),
      .. new[] { Array.Empty<short>(), [-32768, 32767, -1] }.Select(items => Call("sumInt16", items)),
      .. new[] { Array.Empty<float>(), [0.1f, 16777216f, 1f] }.Select(items => Call("sumFloat32", items)),
      .. new[] { Array.Empty<double>(), [0.1, -0.0, 1e308, double.NaN] }.Select(items => Call("scaleFloat64", items)),
      .. new[] { Array.Empty<float>(), [0.1f, -0f, 3e38f, float.NaN] }.Select(items => Call("scaleFloat32", items)),
      .. new[] { Array.Empty<ulong>(), [ulong.MaxValue, 1, 1UL << 63] }.Select(items => Call("xorBigUint64", items)),
      .. new[] { Array.Empty<long>(), [long.MinValue, long.MaxValue, -1, 0] }.Select(items => Call("negateBigInt64", items)),
      .. new[] { Array.Empty<double>(), [0.1, 0.2, 0.3] }.Select(items => Call("sumNumbers", items)),
      .. new[] { 0.0, 1.0, 2.0, -0.0 }.Select(index => Call("readAt", new byte[] { 5, 6, 7 }, index))
    ],
    _ when JavaScriptFixture.Inference.Contains(fixture) => BoundaryCalls(Scalar(fixture)),
    _ => throw new ArgumentException($"There is no fixture '{fixture}'.", nameof(fixture))
  };

  /// <summary>
  /// The calls on which JavaScript and the imported code differ on purpose, each with what node did
  /// with the original source - recorded from node, and checked against it by the acceptance tests -
  /// and what the imported code does instead.
  /// </summary>
  public static IReadOnlyList<JavaScriptDifference> Differences() {
    var differences = new List<JavaScriptDifference>();
    void Add(string caveat, string fixture, string node, string imported, string method, params object[] arguments)
      => differences.Add(new(caveat, fixture, new PrototypeCall(method, arguments), node, imported));

    // js-oob-read: an array of length 0 and of length 1, read and written at -1, at its length and, when empty, at 0.
    var outOfRange = PrototypeFixture.Trap(IrTrap.IndexOutOfRange);
    foreach (var length in new[] { 0, 1 }) {
      var content = length == 0 ? "[]" : "[9]";
      var bytes = Enumerable.Repeat((byte)9, length).ToArray();
      var ints = Enumerable.Repeat(9, length).ToArray();
      var uints = Enumerable.Repeat(9u, length).ToArray();
      foreach (var index in new[] { -1, length }) {
        Add("js-oob-read", "arrays", $"void | {content}", outOfRange, "readByte", bytes, index);
        Add("js-oob-read", "arrays", $"void | {content}", outOfRange, "readInt", ints, index);
        Add("js-oob-read", "arrays", $"void | {content}", outOfRange, "readUint", uints, index);
        Add("js-oob-read", "arrays", $"0 | {content}", outOfRange, "readCoerced", bytes, index);
        Add("js-oob-read", "arrays", $"void | {content}", outOfRange, "writeByte", bytes, index, 7);
        Add("js-oob-read", "arrays", $"void | {content}", outOfRange, "writeInt", ints, index, 7u);
        Add("js-oob-read", "arrays", $"void | {content}", outOfRange, "writeUint", uints, index, 7);
      }
    }

    // js-oob-read: an index that an uncoerced sum takes beyond 32 bits is outside the array in JavaScript
    // and no index at all in the imported code.
    Add("js-oob-read", "arrays", "void | [5,6,7]", PrototypeFixture.Trap(IrTrap.IntegerOverflow), "readAtSum", new uint[] { 5, 6, 7 }, int.MinValue, int.MinValue + 2);

    // js-div-zero: no quotient, and the one quotient that does not fit.
    Add("js-div-zero", "operators", "0", PrototypeFixture.Trap(IrTrap.DivideByZero), "divideInt", 1, 0);
    Add("js-div-zero", "operators", "0", PrototypeFixture.Trap(IrTrap.DivideByZero), "divideUint", 1u, 0u);
    Add("js-div-zero", "operators", "-2147483648", PrototypeFixture.Trap(IrTrap.IntegerOverflow), "divideInt", int.MinValue, -1);
    Add("js-div-zero", "operators", "2147483648", PrototypeFixture.Trap(IrTrap.IntegerOverflow), "divideIntToUint", int.MinValue, -1);

    // js-div-zero: the divisions of the inference fixtures, of 32 and of 64 bits; a BigInt division by zero throws in JavaScript.
    Add("js-div-zero", "inference/conversions", "0", PrototypeFixture.Trap(IrTrap.DivideByZero), "divideInts", 1, 0);
    Add("js-div-zero", "inference/conversions", "0", PrototypeFixture.Trap(IrTrap.DivideByZero), "remainderInts", 1, 0);
    Add("js-div-zero", "inference/conversions", "0", PrototypeFixture.Trap(IrTrap.DivideByZero), "divideUints", 1u, 0u);
    Add("js-div-zero", "inference/conversions", "0", PrototypeFixture.Trap(IrTrap.DivideByZero), "remainderUints", 1u, 0u);
    Add("js-div-zero", "inference/conversions", "0", PrototypeFixture.Trap(IrTrap.IntegerOverflow), "remainderInts", int.MinValue, -1);
    Add("js-div-zero", "inference/conversions", "2147483648", PrototypeFixture.Trap(IrTrap.IntegerOverflow), "divideIntsToUint", int.MinValue, -1);
    Add("js-div-zero", "inference/conversions", "0", PrototypeFixture.Trap(IrTrap.DivideByZero), "divideIntByByte", 1, (byte)0);
    Add("js-div-zero", "inference/bigint", "trap: Division by zero", PrototypeFixture.Trap(IrTrap.DivideByZero), "divide", 1UL, 0UL);
    Add("js-div-zero", "inference/bigint", "trap: Division by zero", PrototypeFixture.Trap(IrTrap.DivideByZero), "remainder", 1UL, 0UL);
    Add("js-div-zero", "inference/bigint", "-9223372036854775808", PrototypeFixture.Trap(IrTrap.IntegerOverflow), "divideSigned", long.MinValue, -1L);
    Add("js-div-zero", "inference/bigint", "0", PrototypeFixture.Trap(IrTrap.IntegerOverflow), "remainderSigned", long.MinValue, -1L);

    // js-oob-read: an index that is no integer reads nothing in JavaScript; the imported code drops the
    // fraction, and traps when what is left is no index at all.
    Add("js-oob-read", "inference/arrays", "0 | [5,6,7]", "6 | [5,6,7]", "readAt", new byte[] { 5, 6, 7 }, 1.5);
    Add("js-oob-read", "inference/arrays", "0 | [5,6,7]", "5 | [5,6,7]", "readAt", new byte[] { 5, 6, 7 }, -0.5);
    Add("js-oob-read", "inference/arrays", "0 | [5,6,7]", outOfRange, "readAt", new byte[] { 5, 6, 7 }, 3.0);
    Add("js-oob-read", "inference/arrays", "0 | [5,6,7]", PrototypeFixture.Trap(IrTrap.IntegerOverflow), "readAt", new byte[] { 5, 6, 7 }, double.NaN);
    Add("js-oob-read", "inference/arrays", "0 | [5,6,7]", PrototypeFixture.Trap(IrTrap.IntegerOverflow), "readAt", new byte[] { 5, 6, 7 }, 4294967296.0);
    return differences;
  }

  /// <summary>The random calls of the operator fixture: a thousand for every function, the same on every run.</summary>
  public static IReadOnlyList<PrototypeCall> RandomOperatorCalls() => Operators(true);

  /// <summary>
  /// The fixtures whose functions are also compared on random operands: the operators on the three
  /// integer types, and every function of the inference fixtures that takes numbers and truth values.
  /// </summary>
  public static IEnumerable<string> RandomFixtures => JavaScriptFixture.Inference.Prepend("operators");

  /// <summary>The random calls of a fixture: a thousand for every function without an array parameter, the same on every run.</summary>
  public static IReadOnlyList<PrototypeCall> RandomCalls(string fixture) => RandomCalls(Scalar(fixture), RandomCallsPerOperator);

  /// <summary>The functions of a fixture that node can be asked for and that take no array.</summary>
  private static List<IrFunction> Scalar(string fixture)
    => JavaScriptFixture.Import(fixture).Functions.Where(function => function.IsPublic && function.Parameters.All(parameter => !parameter.Type.IsArray)).ToList();

  private static PrototypeCall Call(string method, params object[] arguments) => new(method, arguments);

  private static List<PrototypeCall> Rotate() {
    var calls = new List<PrototypeCall>();
    foreach (var method in new[] { "rotateLeft", "rotateRight" })
      foreach (var value in new[] { 0u, 1u, 0x80000001u, 0x12345678u, uint.MaxValue })
        foreach (var count in new[] { -1, 0, 1, 8, 31, 32, 33 })
          calls.Add(Call(method, value, count));

    return calls;
  }

  private static List<PrototypeCall> Arrays() {
    var calls = new List<PrototypeCall>();

    // A single element and four elements; index 0 and length-1, the two ends inside the array.
    foreach (var length in new[] { 1, 4 }) {
      var ints = Enumerable.Range(0, length).Select(index => int.MinValue + index).ToArray();
      var uints = Enumerable.Range(0, length).Select(index => uint.MaxValue - (uint)index).ToArray();
      var bytes = Enumerable.Range(0, length).Select(index => (byte)(255 - index)).ToArray();
      foreach (var index in new[] { 0, length - 1 }.Distinct()) {
        calls.Add(Call("readByte", bytes, index));
        calls.Add(Call("readInt", ints, index));
        calls.Add(Call("readUint", uints, index));
        calls.Add(Call("readCoerced", bytes, index));
        calls.Add(Call("readAtUnsignedIndex", ints, (uint)index));
        calls.Add(Call("readAtByteIndex", ints, (byte)index));
        calls.Add(Call("readAtSum", uints, index + 7, -7));
        calls.Add(Call("readAtSum", uints, int.MaxValue, index - int.MaxValue));
        calls.Add(Call("writeByte", bytes, index, 0x1FF));
        calls.Add(Call("writeByte", bytes, index, -1));
        calls.Add(Call("writeInt", ints, index, uint.MaxValue));
        calls.Add(Call("writeInt", ints, index, 7u));
        calls.Add(Call("writeUint", uints, index, -7));
        calls.Add(Call("writeUint", uints, index, int.MinValue));
        calls.Add(Call("writeSum", bytes, index, int.MaxValue, 1));
        calls.Add(Call("writeSum", bytes, index, 200, 100));
      }
    }

    // The empty array as well, for everything that does not touch an element.
    foreach (var length in new[] { 0, 1, 4 }) {
      calls.Add(Call("lengthOfBytes", new byte[length]));
      calls.Add(Call("lengthOfInts", new int[length]));
      calls.Add(Call("lengthOfUints", new uint[length]));
      calls.Add(Call("countUp", length));
      calls.Add(Call("powers", length));
      calls.Add(Call("ramp", (byte)length));
      calls.Add(Call("sum", Enumerable.Range(1, length).ToArray()));
      calls.Add(Call("reverse", Enumerable.Range(1, length).ToArray()));
      calls.Add(Call("accumulate", Enumerable.Range(1, length).Select(value => (uint)value).ToArray()));
    }

    calls.Add(Call("countUp", 300));
    calls.Add(Call("powers", 25));
    calls.Add(Call("sum", new[] { int.MaxValue, 1 }));
    calls.Add(Call("reverse", new[] { 1, 2, 3, 4, 5 }));
    calls.Add(Call("accumulate", new[] { uint.MaxValue, 1u, 2u }));
    calls.Add(Call("scramble", Array.Empty<byte>(), 5));
    calls.Add(Call("scramble", new byte[] { 1, 2, 3, 4 }, 0));
    calls.Add(Call("scramble", new byte[] { 1, 2, 3, 4, 5, 6 }, -77));
    calls.Add(Call("scramble", new byte[] { 255, 0, 128, 7 }, int.MaxValue));
    foreach (var takeFirst in _bools)
      calls.Add(Call("lengthOfChosen", new byte[2], new byte[3], takeFirst));

    return calls;
  }

  private static List<PrototypeCall> Statements() {
    var calls = new List<PrototypeCall>();
    foreach (var value in new[] { int.MinValue, -5, 0, 5, int.MaxValue }) {
      calls.Add(Call("sign", value));
      calls.Add(Call("clamp", value, -3, 3));
      calls.Add(Call("mix", value));
    }

    // Loops that run zero times, once and many times.
    foreach (var count in new[] { -3, 0, 1, 2, 10, 65536 }) {
      calls.Add(Call("sumTo", count));
      calls.Add(Call("halvings", (uint)count));
    }

    foreach (var count in new[] { 0u, 1u, 2u, 1000u })
      calls.Add(Call("countDown", count));

    foreach (var size in new[] { -1, 0, 1, 5 })
      calls.Add(Call("triangle", size));

    foreach (var value in _bytes.Append((byte)0x5A))
      calls.Add(Call("swapNibbles", value));

    foreach (var n in new[] { -1, 0, 1, 5, 13, 20 })
      calls.Add(Call("factorial", n));

    foreach (var n in new[] { 0, 1, 7, 10 })
      calls.Add(Call("isEven", n));

    foreach (var value in new[] { int.MinValue, -1, 0, int.MaxValue - 1, int.MaxValue })
      calls.Add(Call("successorIsLarger", value));

    foreach (var first in _bools) {
      calls.Add(Call("both", first, new[] { 0 }));
      calls.Add(Call("either", first, new[] { 0 }));
    }

    calls.Add(Call("order", new[] { 0 }));
    calls.Add(Call("fill", Array.Empty<int>(), 7));
    calls.Add(Call("fill", new[] { 0, 0, 0 }, -1));
    calls.Add(Call("indexOf", Array.Empty<byte>(), (byte)1));
    calls.Add(Call("indexOf", new byte[] { 1 }, (byte)1));
    calls.Add(Call("indexOf", new byte[] { 3, 2, 1 }, (byte)1));
    calls.Add(Call("indexOf", new byte[] { 3, 2, 1 }, (byte)9));
    return calls;
  }

  private static List<PrototypeCall> Operators(bool random)
    => random
      ? RandomCalls(JavaScriptFixture.Import("operators").Functions, RandomCallsPerOperator)
      : BoundaryCalls(JavaScriptFixture.Import("operators").Functions);

  /// <summary>For every function all combinations of the boundary values of its parameter types.</summary>
  public static List<PrototypeCall> BoundaryCalls(IEnumerable<IrFunction> functions) {
    var calls = new List<PrototypeCall>();
    foreach (var function in functions) {
      IEnumerable<object[]> combinations = [[]];
      foreach (var parameter in function.Parameters)
        combinations = combinations.SelectMany(_ => Boundaries(parameter.Type), (prefix, value) => prefix.Append(value).ToArray()).ToList();

      calls.AddRange(combinations.Select(arguments => new PrototypeCall(function.Name, arguments)));
    }

    return calls.Where(call => !Divides(call) || IsDefinedDivision(call)).ToList();
  }

  /// <summary>For every function a number of calls with random operands, the same on every run.</summary>
  public static List<PrototypeCall> RandomCalls(IEnumerable<IrFunction> functions, int perFunction) {
    var generator = new Random(Seed);
    var calls = new List<PrototypeCall>();
    foreach (var function in functions) {
      for (var added = 0; added < perFunction;) {
        var call = new PrototypeCall(function.Name, function.Parameters.Select(parameter => RandomOperand(parameter.Type, generator)).ToArray());
        if (Divides(call) && !IsDefinedDivision(call))
          continue;

        calls.Add(call);
        ++added;
      }
    }

    return calls;
  }

  /// <summary>Whether a function divides integers, by its name: the division of the IR traps where JavaScript's does not.</summary>
  private static bool Divides(PrototypeCall call)
    => call.Method.StartsWith("divide", StringComparison.Ordinal) || call.Method.StartsWith("remainder", StringComparison.Ordinal);

  /// <summary>Whether a division has a quotient: a divisor other than zero, and not the one signed overflow of 32 or of 64 bits.</summary>
  private static bool IsDefinedDivision(PrototypeCall call) {
    var dividend = Convert.ToDecimal(call.Arguments[0], System.Globalization.CultureInfo.InvariantCulture);
    var divisor = Convert.ToDecimal(call.Arguments[1], System.Globalization.CultureInfo.InvariantCulture);
    return divisor != 0 && !((dividend == int.MinValue || dividend == long.MinValue) && divisor == -1);
  }

  private static object[] Boundaries(IrType type) => type.Kind switch {
    IrTypeKind.I32 => _ints.Cast<object>().ToArray(),
    IrTypeKind.U32 => _uints.Cast<object>().ToArray(),
    IrTypeKind.U8 => _bytes.Cast<object>().ToArray(),
    IrTypeKind.I8 => [sbyte.MinValue, (sbyte)-1, (sbyte)0, sbyte.MaxValue],
    IrTypeKind.I16 => [short.MinValue, (short)-1, (short)0, short.MaxValue],
    IrTypeKind.U16 => [(ushort)0, (ushort)1, (ushort)0x8000, ushort.MaxValue],
    IrTypeKind.I64 => [long.MinValue, -1L, 0L, 1L, long.MaxValue],
    IrTypeKind.U64 => [0UL, 1UL, (ulong)long.MaxValue, 1UL << 63, ulong.MaxValue],
    IrTypeKind.F32 => _doubles.Select(value => (object)(float)value).Distinct().ToArray(),
    IrTypeKind.F64 => _doubles.Cast<object>().ToArray(),
    _ => _bools.Cast<object>().ToArray()
  };

  /// <summary>A random operand: one time in eight a boundary value, otherwise any value of the type.</summary>
  private static object RandomOperand(IrType type, Random random) {
    var boundaries = Boundaries(type);
    if (random.Next(8) == 0)
      return boundaries[random.Next(boundaries.Length)];

    var bits = (ulong)random.NextInt64(long.MinValue, long.MaxValue);
    return type.Kind switch {
      IrTypeKind.I32 => (int)bits,
      IrTypeKind.U32 => (uint)bits,
      IrTypeKind.U8 => (byte)bits,
      IrTypeKind.I8 => (sbyte)bits,
      IrTypeKind.I16 => (short)bits,
      IrTypeKind.U16 => (ushort)bits,
      IrTypeKind.I64 => (long)bits,
      IrTypeKind.U64 => bits,
      IrTypeKind.F32 => (float)RandomDouble(bits),
      IrTypeKind.F64 => RandomDouble(bits),
      _ => (bits & 1) != 0
    };
  }

  /// <summary>
  /// A random double of one of four kinds: any bit pattern, an integer of up to 33 bits, such an
  /// integer plus a half, and an integer around 2^53, where a double stops holding every one.
  /// </summary>
  private static double RandomDouble(ulong bits) => (bits & 3) switch {
    0 => BitConverter.Int64BitsToDouble((long)bits),
    1 => (long)(bits >> 2) >> 29,
    2 => ((long)(bits >> 2) >> 29) + 0.5,
    _ => ((bits & 4) == 0 ? 1.0 : -1.0) * (9007199254740992.0 + ((long)(bits >> 3) % 4096) - 2048)
  };

}
