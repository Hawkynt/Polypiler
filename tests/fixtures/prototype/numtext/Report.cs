// A line of a report: doubles, floats, integers and truth values as .NET writes them with the
// invariant culture. Written for this repository.
using System.Globalization;

namespace Polypiler.Fixtures.Prototype;

public static class Report {

  public static string Line(double x, float y, long n, bool flag)
    => "x=" + x.ToString(CultureInfo.InvariantCulture) + " y=" + y.ToString(CultureInfo.InvariantCulture)
      + " n=" + n.ToString(CultureInfo.InvariantCulture) + " flag=" + flag;

  public static string Value(double x) => "v=" + x.ToString(CultureInfo.InvariantCulture);

  public static string Single(float x) => x.ToString("R", CultureInfo.InvariantCulture);

  public static string Unsigned(ulong n) => n.ToString(CultureInfo.InvariantCulture);

  public static string Truth(bool flag) => "b=" + flag;

}
