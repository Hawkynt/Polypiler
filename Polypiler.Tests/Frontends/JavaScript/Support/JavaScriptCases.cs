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
    // and, taken modulo 2^32, inside it in the imported code.
    Add("js-oob-read", "arrays", "void | [5,6,7]", "7 | [5,6,7]", "readAtSum", new uint[] { 5, 6, 7 }, int.MinValue, int.MinValue + 2);

    // js-div-zero: no quotient, and the one quotient that does not fit.
    Add("js-div-zero", "operators", "0", PrototypeFixture.Trap(IrTrap.DivideByZero), "divideInt", 1, 0);
    Add("js-div-zero", "operators", "0", PrototypeFixture.Trap(IrTrap.DivideByZero), "divideUint", 1u, 0u);
    Add("js-div-zero", "operators", "NaN", PrototypeFixture.Trap(IrTrap.DivideByZero), "remainderInt", 1, 0);
    Add("js-div-zero", "operators", "NaN", PrototypeFixture.Trap(IrTrap.DivideByZero), "remainderUint", 1u, 0u);
    Add("js-div-zero", "operators", "-2147483648", PrototypeFixture.Trap(IrTrap.IntegerOverflow), "divideInt", int.MinValue, -1);
    Add("js-div-zero", "operators", "2147483648", PrototypeFixture.Trap(IrTrap.IntegerOverflow), "divideIntToUint", int.MinValue, -1);
    Add("js-div-zero", "operators", "0", PrototypeFixture.Trap(IrTrap.IntegerOverflow), "remainderInt", int.MinValue, -1);

    // js-step-wrap: ++ at the largest int32.
    Add("js-step-wrap", "statements", "true", "false", "successorIsLarger", int.MaxValue);
    return differences;
  }

  /// <summary>The random calls of the operator fixture: a thousand for every function, the same on every run.</summary>
  public static IReadOnlyList<PrototypeCall> RandomOperatorCalls() => Operators(true);

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

    foreach (var value in new[] { int.MinValue, -1, 0, int.MaxValue - 1 })
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

  private static bool Divides(PrototypeCall call)
    => call.Method.StartsWith("divide", StringComparison.Ordinal) || call.Method.StartsWith("remainder", StringComparison.Ordinal);

  /// <summary>Whether a division has a quotient: a divisor other than zero, and not the one signed overflow.</summary>
  private static bool IsDefinedDivision(PrototypeCall call) {
    var dividend = Convert.ToInt64(call.Arguments[0], System.Globalization.CultureInfo.InvariantCulture);
    var divisor = Convert.ToInt64(call.Arguments[1], System.Globalization.CultureInfo.InvariantCulture);
    return divisor != 0 && !(dividend == int.MinValue && divisor == -1);
  }

  private static object[] Boundaries(IrType type) => type.Kind switch {
    IrTypeKind.I32 => _ints.Cast<object>().ToArray(),
    IrTypeKind.U32 => _uints.Cast<object>().ToArray(),
    IrTypeKind.U8 => _bytes.Cast<object>().ToArray(),
    _ => _bools.Cast<object>().ToArray()
  };

  /// <summary>A random operand: one time in eight a boundary value, otherwise any value of the type.</summary>
  private static object RandomOperand(IrType type, Random random) {
    var boundaries = Boundaries(type);
    if (random.Next(8) == 0)
      return boundaries[random.Next(boundaries.Length)];

    var bits = (uint)random.NextInt64(1L << 32);
    return type.Kind switch {
      IrTypeKind.I32 => (int)bits,
      IrTypeKind.U32 => bits,
      IrTypeKind.U8 => (byte)bits,
      _ => (bits & 1) != 0
    };
  }

}
