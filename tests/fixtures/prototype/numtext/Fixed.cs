// Fixed notation: .NET rounds the exact value, a tie to the even digit. Written for this repository.
using System.Globalization;

namespace Polypiler.Fixtures.Prototype;

public static class FixedText {

  public static string Two(double x) => x.ToString("F2", CultureInfo.InvariantCulture);

  public static string None(double x) => x.ToString("F0", CultureInfo.InvariantCulture);

  public static string Most(double x) => x.ToString("F15", CultureInfo.InvariantCulture);

  public static string Single(float x) => x.ToString("F3", CultureInfo.InvariantCulture);

  public static string Bracketed(double x) => "[" + x.ToString("F1", CultureInfo.InvariantCulture) + "]";

}
