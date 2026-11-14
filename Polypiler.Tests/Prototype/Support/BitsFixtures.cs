using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Polypiler.Core.IR;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// The fixtures of <c>tests/fixtures/prototype/bits</c>: three whole algorithms that need the integer
/// operations - SipHash-2-4 and xxHash32 in C#, CRC-16/ARC in JavaScript - with their published
/// vectors, and a C# and a JavaScript file per family whose members the frontend maps, with the calls
/// that run them.
/// </summary>
internal static class BitsFixtures {

  /// <summary>The C# fixtures, by the name of their class and file.</summary>
  public static readonly string[] CSharp = [
    "SipHash24", "XxHash32", "BitsRotate", "BitsCounts", "BitsReordering", "BitsPowersOfTwo", "BitsWideMultiply", "BitsDivision",
    "BitsBounds", "BitsConversions", "BitsEndianAccess"
  ];

  /// <summary>The JavaScript fixtures, by the name of their file without the extension, with the types of the parameters of each function.</summary>
  public static readonly IReadOnlyDictionary<string, (string Function, IrType[] Parameters, Type? Returns)[]> JavaScript = new Dictionary<string, (string, IrType[], Type?)[]> {
    ["crc16"] = [("compute", [IrType.Array(IrType.U8)], null)],
    ["counts"] = [("leading", [IrType.U32], null), ("leadingShort", [IrType.I16], null), ("log2", [IrType.U32], null)],
    ["widemultiply"] = [("product", [IrType.I32, IrType.I32], null), ("productUnsigned", [IrType.U32, IrType.U32], null)],
    ["bounds"] = [("magnitude", [IrType.I32], null), ("magnitudeByte", [IrType.I8], null), ("smaller", [IrType.I16, IrType.U16], null),
      ("largest", [IrType.U8, IrType.U8, IrType.U8], null), ("signum", [IrType.I32], null)],
    ["masks"] = [("low12", [IrType.U64], typeof(ulong)), ("signed20", [IrType.I64], typeof(long)), ("wrapped", [IrType.U64], typeof(long))],
    ["endianaccess"] = [("little32", [IrType.Array(IrType.U8), IrType.I32], null), ("big16", [IrType.Array(IrType.U8), IrType.I32], null),
      ("big64", [IrType.Array(IrType.U8), IrType.I32], typeof(ulong)), ("store", [IrType.Array(IrType.U8), IrType.I32, IrType.I32], typeof(byte[]))]
  };

  /// <summary>The names of the JavaScript fixtures.</summary>
  public static string[] Scripts => [.. JavaScript.Keys];

  /// <summary>The directory of the fixtures.</summary>
  public static string Directory { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype", "bits");

  /// <summary>The path of a C# fixture.</summary>
  public static string PathOf(string fixture) => Path.Combine(Directory, fixture + ".cs");

  /// <summary>The path of a JavaScript fixture.</summary>
  public static string ScriptOf(string fixture) => Path.Combine(Directory, fixture + ".js");

  /// <summary>A C# fixture imported into IR.</summary>
  public static IrModule Imported(string fixture) => PrototypeFixture.Import(fixture + ".cs", File.ReadAllText(PathOf(fixture)));

  /// <summary>The class of a C# fixture as the C# compiler built it into the tests.</summary>
  public static Type Compiled(string fixture) => typeof(BitsFixtures).Assembly.GetType("Polypiler.Fixtures.Prototype." + fixture, true)!;

  /// <summary>What the compiled fixture does for a call.</summary>
  public static string CompiledOutcome(string fixture, PrototypeCall call)
    => PrototypeFixture.InvokedOutcome(Compiled(fixture).GetMethod(call.Method, BindingFlags.Public | BindingFlags.Static)!, call);

  /// <summary>The bytes 00, 01, … of a length.</summary>
  public static byte[] Counting(int length) => [.. Enumerable.Range(0, length).Select(value => (byte)value)];

  /// <summary>The calls of a C# fixture: the published vectors of an algorithm, otherwise the edges and random operands of every public method.</summary>
  public static IReadOnlyList<PrototypeCall> Calls(string fixture) {
    var random = new Random(IntegerCases.Seed);
    switch (fixture) {
      case "SipHash24":
        return [
          new("Hash", [Counting(16), Counting(15)]) { Returns = typeof(ulong) },
          new("Hash", [Counting(16), Array.Empty<byte>()]) { Returns = typeof(ulong) },
          .. Enumerable.Range(0, 64).Select(length => new PrototypeCall("Hash", [Counting(16), Counting(length)]) { Returns = typeof(ulong) }),
          .. Enumerable.Range(0, 40).Select(_ => new PrototypeCall("Hash", [Random(random, 16), Random(random, random.Next(0, 40))]) { Returns = typeof(ulong) }),
          new("Hash", [Counting(15), Counting(4)]) { Returns = typeof(ulong) }
        ];
      case "XxHash32":
        return [
          new("Hash", [Array.Empty<byte>(), 0u]),
          new("Hash", [Encoding.ASCII.GetBytes("a"), 0u]),
          new("Hash", [Encoding.ASCII.GetBytes("abc"), 0u]),
          new("Hash", [Encoding.ASCII.GetBytes("Nobody inspects the spammish repetition"), 0u]),
          .. Enumerable.Range(0, 70).Select(length => new PrototypeCall("Hash", [Counting(length), (uint)length * 2654435761u])),
          .. Enumerable.Range(0, 40).Select(_ => new PrototypeCall("Hash", [Random(random, random.Next(0, 70)), (uint)random.NextInt64(0, 1L << 32)]))
        ];
      default:
        return [.. Compiled(fixture).GetMethods(BindingFlags.Public | BindingFlags.Static).OrderBy(method => method.Name, StringComparer.Ordinal)
          .SelectMany(method => Calls(method.Name, [.. method.GetParameters().Select(parameter => Ir(parameter.ParameterType))], method.ReturnType, random))];
    }
  }

  /// <summary>The calls of a function of a JavaScript fixture.</summary>
  public static IReadOnlyList<PrototypeCall> ScriptCalls(string fixture) {
    if (fixture == "crc16")
      return [
        new("compute", [Encoding.ASCII.GetBytes("123456789")]),
        new("compute", [Array.Empty<byte>()]),
        new("compute", [new byte[] { 0x01 }]),
        .. Enumerable.Range(0, 40).Select(length => new PrototypeCall("compute", [Counting(length)]))
      ];

    var random = new Random(IntegerCases.Seed);
    return [.. JavaScript[fixture].SelectMany(function => Calls(function.Function, function.Parameters, function.Returns, random))];
  }

  /// <summary>Every combination of the edges of the parameters, then 200 random tuples.</summary>
  private static IEnumerable<PrototypeCall> Calls(string method, IrType[] parameters, Type? returns, Random random) {
    IEnumerable<object[]> tuples = [[]];
    for (var index = 0; index < parameters.Length; ++index)
      tuples = tuples.SelectMany(tuple => Edges(parameters, index).Select(value => (object[])[.. tuple, value])).ToList();

    var calls = tuples.Select(tuple => new PrototypeCall(method, tuple) { Returns = returns }).ToList();
    for (var count = 0; count < 200; ++count)
      calls.Add(new PrototypeCall(method, [.. parameters.Select((type, index) => RandomOf(parameters, index, random))]) { Returns = returns });

    return calls;
  }

  private static object[] Edges(IrType[] parameters, int index) => parameters[index].Kind switch {
    IrTypeKind.Array => [Counting(11), new byte[] { 0xFF, 0x80 }, new byte[] { 0x7F, 0xFF, 0x00, 0x80, 0x01, 0xFE, 0xAA, 0x55 }],
    IrTypeKind.F64 => [0.0, -2.5, 255.75, -1e19, 1e20, double.NaN],
    IrTypeKind.I32 when parameters[0].IsArray && index == 1 => [-1, 0, 1, 3, 6, 7, 9, 10],
    _ => IntegerCases.Edges(parameters[index])
  };

  private static object RandomOf(IrType[] parameters, int index, Random random) {
    var type = parameters[index];
    if (type.IsArray)
      return Random(random, random.Next(0, 13));

    if (type == IrType.F64)
      return (random.NextDouble() - 0.5) * Math.Pow(2, random.Next(0, 70));

    if (type == IrType.I32 && parameters[0].IsArray && index == 1)
      return random.Next(-1, 14);

    var pattern = (ulong)random.NextInt64(long.MinValue, long.MaxValue);
    return IntegerCases.FromPattern(type, random.Next(3) == 0 ? pattern % 300 : pattern);
  }

  private static byte[] Random(Random random, int length) {
    var bytes = new byte[length];
    random.NextBytes(bytes);
    return bytes;
  }

  private static IrType Ir(Type type) => Type.GetTypeCode(type) switch {
    TypeCode.SByte => IrType.I8,
    TypeCode.Byte => IrType.U8,
    TypeCode.Int16 => IrType.I16,
    TypeCode.UInt16 => IrType.U16,
    TypeCode.Int32 => IrType.I32,
    TypeCode.UInt32 => IrType.U32,
    TypeCode.Int64 => IrType.I64,
    TypeCode.UInt64 => IrType.U64,
    TypeCode.Double => IrType.F64,
    _ => IrType.Array(IrType.U8)
  };

}
