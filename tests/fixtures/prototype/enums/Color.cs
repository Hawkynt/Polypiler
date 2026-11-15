// An enum over byte: arithmetic wraps in the byte, a value no member has keeps its number, and a
// switch chooses by the members. Written for this repository.
using System;

namespace Polypiler.Fixtures.Prototype;

public static class Colors {

  public enum Color : byte { Red, Green = 5, Blue }

  // Blue + 250 is 256, which the byte wraps to 0: Red.
  public static string Shift(Color c, byte n) => (c + n).ToString();

  public static string Cast(int v) => ((Color)v).ToString();

  public static bool IsDefined(byte v) => Enum.IsDefined(typeof(Color), v);

  public static int Parse(string s) => (int)(Color)Enum.Parse(typeof(Color), s, true);

  public static int Shade(Color c) {
    switch (c) {
      case Color.Red:
        return 10;
      case Color.Green:
        return 20;
      default:
        return 30;
    }
  }

}
