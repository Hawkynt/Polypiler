// The members whose meaning differs between C# and JavaScript, one function each: what each must
// keep of its source when it is translated. Written for this repository.
namespace Polypiler.Fixtures.Prototype;

public static class Traps {

  // Every occurrence, where JavaScript's replace takes the first.
  public static string ReplaceAll(string s) => s.Replace("b", "XY");

  // .NET's white space: U+0085 is, U+FEFF is not.
  public static string TrimNet(string s) => s.Trim();

  // Code units, where JavaScript's for...of counts code points.
  public static int CountUnits(string s) {
    int count = 0;
    foreach (char unit in s)
      count++;

    return count;
  }

  public static bool Less(string a, string b) => string.CompareOrdinal(a, b) < 0;

  public static bool StartsOrdinal(string s) => s.StartsWith("e", System.StringComparison.Ordinal);

  public static char At(string s, int index) => s[index];

  public static string Cut(string s, int start, int length) => s.Substring(start, length);

  public static string Pad(string s) => s.PadLeft(5, '.');

  public static string Repeat(char unit, int count) => new string(unit, count);

  public static int LastComma(string s) => s.LastIndexOf(',');

}
