using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Float.Support;

/// <summary>How the exact result of a reference vector relates to the values of its format.</summary>
internal enum FloatVectorKind {

  /// <summary>The exact result is a value of the format, or an infinity or a zero.</summary>
  Exact,

  /// <summary>The result is a NaN.</summary>
  NaN,

  /// <summary>The exact result lies strictly between two values of the format.</summary>
  Inexact

}

/// <summary>
/// One line of a file of <c>tests/vectors/float</c>: the operands, the correctly rounded result and,
/// for an inexact result, the value next to the exact one on the side of zero and the position of the
/// exact result between it and its neighbour away from zero.
/// </summary>
/// <param name="Class">The class of the input: core, domain, special, edge, reduction or worst.</param>
/// <param name="Operands">The bits of the operands; the integer operand of <c>rootn</c> as its value.</param>
/// <param name="Result">The bits of the correctly rounded result.</param>
/// <param name="Kind">Exact, NaN or inexact.</param>
/// <param name="Low">The bits of the value next to the exact result towards zero; 0 unless inexact.</param>
/// <param name="Fraction">The position of the exact result above <paramref name="Low"/>, in (0, 1); 0 unless inexact.</param>
/// <param name="Line">The line in the file.</param>
internal sealed record FloatVector(string Class, long[] Operands, long Result, FloatVectorKind Kind, long Low, double Fraction, int Line) {

  /// <summary>The vector as a failure message shows it.</summary>
  public override string ToString() => $"line {this.Line} ({this.Class}): {string.Join(", ", this.Operands.Select(operand => operand.ToString("x", CultureInfo.InvariantCulture)))}";

}

/// <summary>Reads the reference vectors and measures results against them.</summary>
internal static class FloatVectors {

  private static readonly Dictionary<string, IReadOnlyList<FloatVector>> _cache = [];

  /// <summary>The folder of the vector files.</summary>
  public static string Folder { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "vectors", "float");

  /// <summary>The vectors of a function in a format.</summary>
  /// <param name="function">The name the generator gives it: <c>sin</c>, <c>atan2</c>, <c>fma</c>, …</param>
  /// <param name="format"><see cref="IrType.F32"/> or <see cref="IrType.F64"/>.</param>
  /// <returns>The vectors in file order.</returns>
  public static IReadOnlyList<FloatVector> Of(string function, IrType format) {
    var name = $"{function}.{format}.txt";
    lock (_cache) {
      if (_cache.TryGetValue(name, out var cached))
        return cached;

      var integer = function == "rootn";
      var vectors = new List<FloatVector>();
      var number = 0;
      foreach (var line in File.ReadLines(Path.Combine(Folder, name))) {
        ++number;
        if (line.StartsWith('#'))
          continue;

        var parts = line.Split(' ');
        var kindIndex = Array.FindIndex(parts, part => part is "exact" or "nan" or "inexact");
        var operands = parts[1..(kindIndex - 1)]
          .Select((part, index) => integer && index == 1 ? long.Parse(part, CultureInfo.InvariantCulture) : Hex(part))
          .ToArray();
        var kind = parts[kindIndex] switch {
          "exact" => FloatVectorKind.Exact,
          "nan" => FloatVectorKind.NaN,
          _ => FloatVectorKind.Inexact
        };
        vectors.Add(new FloatVector(
          parts[0],
          operands,
          Hex(parts[kindIndex - 1]),
          kind,
          kind == FloatVectorKind.Inexact ? Hex(parts[kindIndex + 1]) : 0,
          kind == FloatVectorKind.Inexact ? double.Parse(parts[kindIndex + 2], CultureInfo.InvariantCulture) : 0,
          number));
      }

      _cache[name] = vectors;
      return vectors;
    }
  }

  /// <summary>The number a vector operand or result stands for in a format.</summary>
  public static double Value(IrType format, long bits) => IrOperators.Float(format, bits);

  /// <summary>The bits of a number in a format.</summary>
  public static long Bits(IrType format, double value) => IrOperators.Bits(format, value);

  /// <summary>Whether bits of a format are a NaN.</summary>
  public static bool IsNaN(IrType format, long bits) => double.IsNaN(Value(format, bits));

  /// <summary>
  /// Whether a computed result matches the correctly rounded one exactly, under the special-value rule
  /// <c>signed-zero</c> of ADR 0005: any NaN equals any NaN, otherwise the bits are compared.
  /// </summary>
  public static bool Same(IrType format, long expected, long actual)
    => IsNaN(format, expected) ? IsNaN(format, actual) : Mask(format, expected) == Mask(format, actual);

  /// <summary>
  /// The comparison distance of ADR 0005 between two finite values of a format: the number of steps
  /// between them, both zeros counting as one point.
  /// </summary>
  public static long Distance(IrType format, long a, long b) => Math.Abs(Ordinal(format, a) - Ordinal(format, b));

  /// <summary>
  /// The reference error of ADR 0005 of a computed value against an inexact vector, in ULP of the
  /// exact result: <c>|(c - lo) / (hi - lo) - frac|</c>.
  /// </summary>
  public static double ReferenceError(IrType format, FloatVector vector, long actual) {
    var low = Value(format, vector.Low);
    var high = Value(format, vector.Low + 1);
    return Math.Abs((Value(format, actual) - low) / (high - low) - vector.Fraction);
  }

  private static long Mask(IrType format, long bits) => format == IrType.F64 ? bits : bits & 0xffffffffL;

  private static long Ordinal(IrType format, long bits) {
    var masked = Mask(format, bits);
    var sign = format == IrType.F64 ? long.MinValue : 0x80000000L;
    var magnitude = format == IrType.F64 ? masked & long.MaxValue : masked & 0x7fffffffL;
    return (masked & sign) != 0 ? -magnitude : magnitude;
  }

  private static long Hex(string text) => (long)ulong.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

}
