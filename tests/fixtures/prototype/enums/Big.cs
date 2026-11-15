// An enum over ulong, which JavaScript below ECMAScript 2020 holds as pairs of halves: the enum is
// private to the class, so its members are written where they are used. Written for this repository.
using System;

namespace Polypiler.Fixtures.Prototype;

public static class BigMembers {

  internal enum Big : ulong { Max = ulong.MaxValue, One = 1, Half = 9223372036854775808 }

  public static string Text(ulong v) => ((Big)v).ToString();

  public static bool IsMax(ulong v) => (Big)v == Big.Max;

  public static ulong Parse(string s) => (ulong)(Big)Enum.Parse(typeof(Big), s);

  public static string Names() => string.Join(",", Enum.GetNames(typeof(Big)));

  public static bool HasOne(ulong v) => ((Big)v).HasFlag(Big.One);

}
