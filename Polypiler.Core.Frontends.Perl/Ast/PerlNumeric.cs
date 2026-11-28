using System;
using System.Globalization;

namespace Polypiler.Core.Frontends.Perl.Ast;

/// <summary>The kinds of a Perl number: a signed integer (IV), an unsigned one beyond the signed range (UV), a binary64 (NV).</summary>
internal enum PerlNumericKind {
  Integer, Unsigned, Float
}

/// <summary>A Perl number as perl keeps it: an IV, a UV above <see cref="long.MaxValue"/> or an NV.</summary>
internal readonly record struct PerlNumeric(PerlNumericKind Kind, long Bits) {

  /// <summary>A signed integer.</summary>
  public static PerlNumeric Of(long value) => new(PerlNumericKind.Integer, value);

  /// <summary>An unsigned integer: an IV where it fits, a UV beyond.</summary>
  public static PerlNumeric OfUnsigned(ulong value) => value <= long.MaxValue ? Of((long)value) : new(PerlNumericKind.Unsigned, unchecked((long)value));

  /// <summary>A binary64.</summary>
  public static PerlNumeric Of(double value) => new(PerlNumericKind.Float, BitConverter.DoubleToInt64Bits(value));

  /// <summary>The value of an integer.</summary>
  public long Integer => this.Bits;

  /// <summary>The value of an unsigned integer.</summary>
  public ulong Unsigned => unchecked((ulong)this.Bits);

  /// <summary>The value as a binary64.</summary>
  public double Double => this.Kind switch {
    PerlNumericKind.Float => BitConverter.Int64BitsToDouble(this.Bits),
    PerlNumericKind.Unsigned => this.Unsigned,
    _ => this.Bits
  };

  /// <summary>Whether the number is an integer (IV or UV).</summary>
  public bool IsIntegral => this.Kind != PerlNumericKind.Float;

  /// <inheritdoc/>
  public override string ToString() => this.Kind switch {
    PerlNumericKind.Integer => this.Bits.ToString(CultureInfo.InvariantCulture),
    PerlNumericKind.Unsigned => this.Unsigned.ToString(CultureInfo.InvariantCulture),
    _ => this.Double.ToString("R", CultureInfo.InvariantCulture)
  };

}
