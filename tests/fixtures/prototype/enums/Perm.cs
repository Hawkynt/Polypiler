// A flags enum: the text of a value is the names of the flags it consists of, the largest first
// taken and written last, or its number where they do not cover it; Parse reads names separated by
// commas, or a number. Written for this repository.
using System;

namespace Polypiler.Fixtures.Prototype;

public static class Permissions {

  [Flags]
  public enum Perm { None = 0, Read = 1, Write = 2, Exec = 4, ReadWrite = 3 }

  // 0 None, 3 ReadWrite, 5 Read, Exec, 7 ReadWrite, Exec, 8 8.
  public static string Describe(int v) => ((Perm)v).ToString();

  public static int F(string s) => (int)(Perm)System.Enum.Parse(typeof(Perm), s);

  public static bool Has(int v, int flag) => ((Perm)v).HasFlag((Perm)flag);

  public static int Grant(int v) {
    var p = (Perm)v;
    p |= Perm.Exec;
    p &= ~Perm.Write;
    return (int)p;
  }

}
