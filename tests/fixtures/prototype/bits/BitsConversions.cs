// The conversions of generic math between integer types. Written for this repository.
using System;
using System.Buffers.Binary;
using System.Numerics;

namespace Polypiler.Fixtures.Prototype;

public static class BitsConversions {

  public static byte Saturated(int x) => byte.CreateSaturating(x);

  public static sbyte SaturatedFromUnsigned(ulong x) => sbyte.CreateSaturating(x);

  public static short Truncated(long x) => short.CreateTruncating(x);

  public static uint Checked(int x) => uint.CreateChecked(x);

  public static int SaturatedFromDouble(double x) => int.CreateSaturating(x);

}
