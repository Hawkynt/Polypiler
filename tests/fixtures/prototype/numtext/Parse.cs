// Text to numbers by .NET's grammar with the invariant culture: thousands separators, Infinity and
// NaN in any case, a malformed text or an integer out of range throws. Written for this repository.
using System.Globalization;

namespace Polypiler.Fixtures.Prototype;

public static class ParseText {

  public static double Double(string text) => double.Parse(text, CultureInfo.InvariantCulture);

  public static float Single(string text) => float.Parse(text, CultureInfo.InvariantCulture);

  public static int Int(string text) => int.Parse(text, CultureInfo.InvariantCulture);

  public static uint UInt(string text) => uint.Parse(text, CultureInfo.InvariantCulture);

  public static long Long(string text) => long.Parse(text, CultureInfo.InvariantCulture);

  public static ulong ULong(string text) => ulong.Parse(text, CultureInfo.InvariantCulture);

  public static byte Byte(string text) => byte.Parse(text, CultureInfo.InvariantCulture);

}
