using System.Collections.Generic;
using System.IO;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.VisualBasic;

/// <summary>
/// The Visual Basic .NET fixtures of <c>tests/fixtures/visualbasic/vbnet</c> and their calls: the
/// happy path of every procedure, the overflow, division and bound traps Visual Basic raises, and the
/// boundaries of the narrow types, banker's rounding, the counter of a <c>For</c> past its type's
/// largest value and the clauses of <c>Select Case</c>.
/// </summary>
internal static class VbNetCases {

  /// <summary>The folder of the fixtures.</summary>
  public static string Folder { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "visualbasic", "vbnet");

  /// <summary>The fixtures, by name.</summary>
  public static IReadOnlyList<string> Names { get; } = ["Numeric", "Control", "Records", "Jumps"];

  /// <summary>The text of a fixture.</summary>
  public static string Source(string name) => File.ReadAllText(Path.Combine(Folder, name + ".vb"));

  private static PrototypeCall Call(string method, params object[] arguments) => new(method, arguments);

  /// <summary>The calls of a fixture.</summary>
  public static IReadOnlyList<PrototypeCall> For(string name) => name switch {
    "Numeric" => [
      Call("AddInt", 2147483647, 1), Call("AddInt", 5, 7), Call("AddInt", -2147483648, -1), Call("MulLong", long.MaxValue, 2L), Call("MulLong", 3L, -4L),
      Call("IntDiv", -7, 2), Call("IntDiv", int.MinValue, -1), Call("IntDiv", 1, 0), Call("Modulo", -7, 2), Call("Modulo", 7, -2), Call("Modulo", 1, 0),
      Call("FloatMod", -7.5, 2.0), Call("FloatMod", 1.0, 0.0), Call("Rounded", 2.5), Call("Rounded", 3.5), Call("Rounded", -2.5), Call("Rounded", 1e10),
      Call("Rounded", double.NaN), Call("Rounded", 2147483647.4), Call("Rounded", 2147483647.5), Call("Truncated", -2.7), Call("Floored", -2.5),
      Call("ByteSum", (byte)200, (byte)100), Call("ByteSum", (byte)200, (byte)55), Call("ByteSum", (byte)1, (byte)2), Call("ShortProduct", (short)300, (short)300),
      Call("ShortProduct", (short)-3, (short)5), Call("ShortProduct", (short)-128, (short)256), Call("ByteShift", (byte)0x81, 1), Call("ByteShift", (byte)1, 9),
      Call("Unsigned", 1u, 2u), Call("Unsigned", 10u, 3u), Call("MixedSigns", 4000000000u, -1), Call("TrueValue", true), Call("TrueValue", false),
      Call("TrueByte", true), Call("ShortCircuit", false, true), Call("ShortCircuit", true, true), Call("Power", 2.0, 10.0), Call("Power", -8.0, 1.0 / 3),
      Call("Narrow", 1L << 40), Call("Narrow", -5L), Call("Narrow", (long)int.MinValue), Call("Compound", (byte)10, 3), Call("Compound", (byte)10, 6),
      Call("Compound", (byte)5, 5), Call("Bits", 0x1234, 0x0F0F), Call("Negated", int.MinValue), Call("Negated", 5)
    ],
    "Control" => [
      Call("Classify", -5), Call("Classify", 0), Call("Classify", 1), Call("Classify", 9), Call("Classify", 10), Call("Classify", 20), Call("Classify", 99),
      Call("Weekday", 1), Call("Weekday", 2), Call("Weekday", 4), Call("Weekday", 7), Call("Weekday", 8), Call("Named", "one"), Call("Named", "zwei"),
      Call("Named", "x"), Call("SumStep", 1, 50, 1), Call("SumStep", 10, 1, -1), Call("SumStep", 1, 100, 7), Call("SumStep", 5, 1, 1), Call("Countdown", 9),
      Call("Countdown", 0), Call("PastTheEnd", int.MaxValue - 2), Call("Collatz", 27L), Call("Collatz", 1L), Call("Halvings", 1000), Call("Halvings", 0),
      Call("WhileSum", 10), Call("Kept", 5), Call("Total", new[] { 1, 2, 3 }), Call("Total", new int[0]), Call("Total", new[] { int.MaxValue, 1 }),
      Call("Vowels", "education"), Call("Vowels", "")
    ],
    "Jumps" => [
      Call("Triangle", 0), Call("Triangle", 1), Call("Triangle", 10), Call("FirstProduct", 0), Call("FirstProduct", 30), Call("FirstProduct", 1000),
      Call("Quotient", 0), Call("Quotient", 7), Call("Retried", 0), Call("Retried", 3), Call("Element", 0), Call("Element", 2), Call("Element", 3),
      Call("Element", -1), Call("Skipped", 0), Call("Skipped", 1), Call("Skipped", 5), Call("Skipped", -1), Call("Branched", 0), Call("Branched", 1),
      Call("Branched", 5), Call("Raised", 1), Call("Raised", 2), Call("Raised", 3), Call("Twice", 0), Call("Twice", 2)
    ],
    _ => [
      Call("Distance", 3, 4), Call("Distance", -3, 1000000000), Call("Darkness", true), Call("Darkness", false), Call("Squares", 4), Call("Squares", 0),
      Call("Walk", 5), Call("Walk", 0), Call("Greeting", "ab", 3), Call("Greeting", "x", 0), Call("SafeDivide", 7, 2), Call("SafeDivide", 1, 0),
      Call("SafeDivide", -1, 0), Call("Checked", 70000), Call("Checked", 5), Call("Raise", -1), Call("Raise", 1), Call("Rethrown", 0), Call("Rethrown", 5)
    ]
  };

}
