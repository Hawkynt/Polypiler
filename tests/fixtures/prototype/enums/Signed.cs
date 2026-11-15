// An enum with a negative member: .NET lists the members by their unsigned values, so -1, which is
// 0xFFFFFFFF, comes last. Written for this repository.
using System;

namespace Polypiler.Fixtures.Prototype;

public static class SignedMembers {

  public enum S { B = 2, Neg = -1, A = 0, C = 1 }

  // A,C,B,Neg
  public static string Names() => string.Join(",", Enum.GetNames(typeof(S)));

  public static string Values() {
    var text = "";
    foreach (var v in (S[])Enum.GetValues(typeof(S)))
      text += (int)v + ";";
    return text;
  }

  public static string Text(int v) => ((S)v).ToString();

  public static bool Less(int a, int b) => (S)a < (S)b;

}
