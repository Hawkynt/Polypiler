using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// The fixtures of <c>tests/fixtures/prototype/numtext</c> and the calls every implementation is
/// compared on: the values of the issue, the boundaries of the layouts, the ties of the fixed
/// notations, the classes of texts of every grammar and numbers of the reference vectors. The C#
/// compiler's build of a C# fixture in this assembly, and <c>node</c> running a JavaScript fixture,
/// are the oracles.
/// </summary>
internal static class NumberTextFixtures {

  /// <summary>The C# fixtures: each class with the file it is in, without the extension.</summary>
  public static readonly (string Class, string File)[] CSharp = [("Report", "Report"), ("HexText", "Hex"), ("FixedText", "Fixed"), ("ParseText", "Parse")];

  /// <summary>The JavaScript fixtures by file name, without the extension.</summary>
  public static readonly string[] JavaScript = ["report", "hex", "fixed", "parse"];

  /// <summary>The path of a fixture file of the folder, its extension included.</summary>
  public static string PathOf(string file) => Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype", "numtext", file);

  /// <summary>A method of a C# fixture as the C# compiler built it into this assembly.</summary>
  public static MethodInfo Compiled(string className, string method)
    => typeof(NumberTextFixtures).Assembly.GetType("Polypiler.Fixtures.Prototype." + className, true)!.GetMethod(method, BindingFlags.Public | BindingFlags.Static)
      ?? throw new MissingMethodException(className, method);

  /// <summary>The doubles every formatting is called with: thresholds, ties, extremes, and doubles of the reference vectors.</summary>
  public static IEnumerable<double> Doubles(int vectors) => new[] {
    0.0, -0.0, 1e-7, 1e-6, 1.5e-6, 1e-5, 1e-4, 1e16, 1e17, 1e20, 1e21, -1e21, 5e-324, double.MaxValue, double.NaN, double.PositiveInfinity, double.NegativeInfinity,
    0.5, 1.5, 2.5, -2.5, 0.125, 1.005, 0.045, 999.995, 0.1 + 0.2, 123.456, -7
  }.Concat(Vectors("f64", vectors).Select(BitConverter.UInt64BitsToDouble));

  /// <summary>The floats every formatting of a float is called with.</summary>
  public static IEnumerable<float> Singles(int vectors) => new[] { 0f, -0f, 0.1f, 16777217f, 1e10f, 1e8f, 1e9f, 1e-5f, 1e-4f, float.MaxValue, float.Epsilon, 170739.125f, float.NaN }
    .Concat(Vectors("f32", vectors).Select(bits => BitConverter.UInt32BitsToSingle((uint)bits)));

  /// <summary>The integers of the bounds of every width.</summary>
  public static readonly long[] Integers = [0, 1, -1, 15, 255, -128, 32767, -32768, int.MaxValue, int.MinValue, long.MaxValue, long.MinValue];

  /// <summary>The texts every parser is called with: the classes of the test design.</summary>
  public static readonly string[] Texts = [
    "", " ", "0", "-0", "+5", " 12 ", ".5", "5.", "1e3", "1.5E-3", "1e", "12px", "0x10", "0x1A", "0b101", "0o17", "1,000", "1,000.5", "1_000",
    "Infinity", "-Infinity", "infinity", "NaN", "nan", "∞", "٣", "1e400", "-1e-400", "2147483647", "2147483648", "-2147483648", "4294967295",
    "9223372036854775807", "9223372036854775808", "18446744073709551615", "-1", "255", "256", "99999999999x", "zz", "3.4028235e38", "3.4028236e38",
    "0.1", "123456789012345678901234567890"
  ];

  /// <summary>The bits of the first entries of a kind of the reference vectors.</summary>
  private static IEnumerable<ulong> Vectors(string kind, int count)
    => File.ReadLines(Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "vectors", "numtext", "shortest.txt"))
      .Where(line => line.StartsWith(kind + " ", StringComparison.Ordinal)).Take(count)
      .Select(line => ulong.Parse(line.Split(' ')[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture));

  /// <summary>The calls of the C# fixtures, each with the class it calls and the type the method returns.</summary>
  /// <param name="vectors">How many doubles and floats of the reference vectors are added to the fixed values.</param>
  public static IEnumerable<(string Class, PrototypeCall Call)> CSharpCalls(int vectors = 20) {
    (string, PrototypeCall) Call(string className, string method, params object[] arguments)
      => (className, new PrototypeCall(method, arguments) { Returns = Compiled(className, method).ReturnType });

    foreach (var value in Doubles(vectors)) {
      yield return Call("Report", "Value", value);
      yield return Call("FixedText", "Two", value);
      yield return Call("FixedText", "None", value);
      yield return Call("FixedText", "Most", value);
      yield return Call("FixedText", "Bracketed", value);
    }

    foreach (var value in Singles(vectors)) {
      yield return Call("Report", "Single", value);
      yield return Call("FixedText", "Single", value);
    }

    yield return Call("Report", "Line", 1e21, 0.1f, long.MinValue, true);
    yield return Call("Report", "Line", -0.0, 16777217f, 0L, false);
    yield return Call("Report", "Unsigned", ulong.MaxValue);
    yield return Call("Report", "Truth", true);
    foreach (var value in Integers) {
      yield return Call("HexText", "Word", (int)value);
      yield return Call("HexText", "Upper", (int)value);
      yield return Call("HexText", "Byte", (byte)value);
      yield return Call("HexText", "Short", (short)value);
      yield return Call("HexText", "Long", value);
      yield return Call("HexText", "Binary", (int)value);
      yield return Call("HexText", "Octal", value);
      yield return Call("HexText", "Padded", (int)value);
    }

    foreach (var text in Texts)
      foreach (var method in new[] { "Double", "Single", "Int", "UInt", "Long", "ULong", "Byte" })
        yield return Call("ParseText", method, text);
  }

  /// <summary>The calls of the JavaScript fixtures, each with the file it calls; the outcomes are what node prints.</summary>
  public static IEnumerable<(string File, PrototypeCall Call)> JavaScriptCalls(int vectors = 20) {
    (string, PrototypeCall) Call(string file, string method, params object[] arguments) => (file, new PrototypeCall(method, arguments));
    foreach (var value in Doubles(vectors)) {
      yield return Call("report", "value", value);
      yield return Call("report", "line", value, 7, value > 0);
      yield return Call("fixed", "two", value);
      yield return Call("fixed", "none", value);
      yield return Call("fixed", "most", value);
    }

    yield return Call("report", "truth", false);
    yield return Call("report", "big", 18446744073709551615UL);
    foreach (var value in Integers) {
      yield return Call("hex", "hexadecimal", (int)value);
      yield return Call("hex", "binary", (int)value);
      yield return Call("hex", "base36", (int)value);
      yield return Call("hex", "unsigned", (int)value);
      yield return Call("hex", "wide", value);
    }

    foreach (var text in Texts)
      foreach (var method in new[] { "number", "plus", "float", "integer", "hex", "base36" })
        yield return Call("parse", method, text);
  }

}
