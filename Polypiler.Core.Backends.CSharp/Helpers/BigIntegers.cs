// SPDX-License-Identifier: MIT
namespace Polypiler.Core.Backends.CSharp;

/// <summary>
/// The helpers of the arbitrary-precision integer, which is a <c>System.Numerics.BigInteger</c> from
/// .NET Framework 4.0 on. Its explicit conversion to <c>double</c> cuts the bits below the 53 it
/// keeps instead of rounding them, on .NET 10 as on .NET Framework: <see cref="BigToDouble"/> rounds
/// to nearest, ties to even, as the IR's conversion does. It is written against the API of .NET
/// Framework 4.0, which no profile without the type calls.
/// </summary>
public static class CSharpBigIntegerHelpers {

  private const string Big = "global::System.Numerics.BigInteger";

  /// <summary>
  /// The nearest <c>double</c> to an integer: the magnitude cut to 55 bits, the lowest of them set when
  /// a bit cut off was, converted from <c>long</c> with the one rounding of the hardware, and scaled by
  /// a power of two, which is exact or overflows to an infinity.
  /// </summary>
  public static CSharpHelper BigToDouble { get; } = new("BigToDouble", "double", Big + " value", [
    Big + " magnitude = " + Big + ".Abs(value);",
    "byte[] bytes = magnitude.ToByteArray();",
    "int bits = (bytes.Length - 1) * 8;",
    "for (int top = bytes[bytes.Length - 1]; top != 0; top >>= 1) {",
    "  bits++;",
    "}",
    "int cut = bits > 55 ? bits - 55 : 0;",
    "long kept = (long)(magnitude >> cut);",
    "if (cut > 0 && !(magnitude & ((" + Big + ".One << cut) - " + Big + ".One)).IsZero) {",
    "  kept |= 1;",
    "}",
    "double result = cut > 1100 ? double.PositiveInfinity : (double)kept * global::System.Math.Pow(2.0, cut);",
    "return value.Sign < 0 ? -result : result;"
  ]);

}
