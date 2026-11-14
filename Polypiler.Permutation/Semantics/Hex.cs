using System.Globalization;
using System.Numerics;

namespace Polypiler.Permutation.Semantics;

/// <summary>Formats masks and inputs for messages and reads single bits of them.</summary>
internal static class Hex {

  /// <summary><c>0x</c> and the digits without leading zeros; a negative value gets a leading minus.</summary>
  internal static string Format(BigInteger value) {
    if (value.Sign < 0)
      return "-" + Format(-value);

    var digits = value.ToString("X", CultureInfo.InvariantCulture).TrimStart('0');
    return "0x" + (digits.Length == 0 ? "0" : digits);
  }

  /// <summary>Whether a mask has a bit at or above <paramref name="width"/>; a negative mask has infinitely many.</summary>
  internal static bool Exceeds(BigInteger mask, int width) => mask.Sign < 0 || mask.GetBitLength() > width;

  /// <summary>Whether bit <paramref name="bit"/> of a mask is set.</summary>
  internal static bool Has(BigInteger mask, int bit) => !((mask >> bit) & BigInteger.One).IsZero;

}
