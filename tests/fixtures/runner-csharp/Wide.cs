using System;

static class T {
  public static ulong Ulong(ulong a) => a;
  public static double G(double a) => a;
  public static float Single(float a) => a;
  public static int N(int[]? a) => a == null ? -1 : a.Length;
  public static byte[] Reverse(byte[] data) {
    var copy = (byte[])data.Clone();
    Array.Reverse(copy);
    return copy;
  }
}
