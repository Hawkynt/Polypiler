using System;
static class P {
  static string Try(Func<object> f) { try { return Convert.ToString(f(), System.Globalization.CultureInfo.InvariantCulture) ?? ""; } catch (Exception e) { return e.GetType().Name; } }
  static void Main() {
    int max = int.MaxValue, min = int.MinValue, one = 1, zero = 0, m1 = -1, s32 = 32, s33 = 33;
    long lmin = long.MinValue; long lm1 = -1;
    double big = 3e9, nbig = -3e9, nan = double.NaN, inf = double.PositiveInfinity;
    Console.WriteLine("add unchecked: " + Try(() => unchecked(max + one)) + "  checked: " + Try(() => checked(max + one)));
    Console.WriteLine("sub unchecked: " + Try(() => unchecked(min - one)) + "  checked: " + Try(() => checked(min - one)));
    Console.WriteLine("mul unchecked: " + Try(() => unchecked(65536 * (one * 65536))) + "  checked: " + Try(() => checked(65536 * (one * 65536))));
    Console.WriteLine("neg unchecked: " + Try(() => unchecked(-min)) + "  checked: " + Try(() => checked(-min)));
    Console.WriteLine("1 << 32: " + (one << s32) + "  1 << 33: " + (one << s33) + "  1 << -1: " + (one << m1));
    Console.WriteLine("-8 >> 33: " + (-8 >> s33) + "  -8 >>> 33: " + (-8 >>> s33) + "  1L << 64: " + (1L << (64 * one)) + "  1L << 65: " + (1L << (65 * one)));
    Console.WriteLine("7 / 0: " + Try(() => 7 / zero) + "  7 % 0: " + Try(() => 7 % zero));
    Console.WriteLine("MIN / -1: " + Try(() => unchecked(min / m1)) + "  MIN % -1: " + Try(() => unchecked(min % m1)));
    Console.WriteLine("long MIN / -1: " + Try(() => unchecked(lmin / lm1)) + "  long MIN % -1: " + Try(() => unchecked(lmin % lm1)));
    Console.WriteLine("-7 / 3: " + (-7 / (one * 3)) + "  -7 % 3: " + (-7 % (one * 3)) + "  7 % -3: " + (7 % (m1 * 3)));
    Console.WriteLine("(int)3e9: " + Try(() => unchecked((int)big)) + "  (int)-3e9: " + Try(() => unchecked((int)nbig)) + "  (int)NaN: " + Try(() => unchecked((int)nan)) + "  (int)Inf: " + Try(() => unchecked((int)inf)));
    Console.WriteLine("checked (int)3e9: " + Try(() => checked((int)big)) + "  checked (int)NaN: " + Try(() => checked((int)nan)));
    Console.WriteLine("(uint)-1.0: " + Try(() => unchecked((uint)(double)m1)) + "  (byte)300.0: " + Try(() => unchecked((byte)(300.0 * one))) + "  (int)2.7: " + (int)(2.7 * one) + "  (int)-2.7: " + (int)(-2.7 * one));
    Console.WriteLine("1.0 / 0: " + Try(() => 1.0 / zero) + "  -7.5 % 2: " + Try(() => -7.5 % (2 * one)));
    Console.WriteLine("(byte)300: " + Try(() => unchecked((byte)(300 * one))) + "  checked (byte)300: " + Try(() => checked((byte)(300 * one))));
    Console.WriteLine("Math.Round(2.5): " + Math.Round(2.5 * one) + "  Math.Round(2.5, AwayFromZero): " + Math.Round(2.5 * one, MidpointRounding.AwayFromZero) + "  Convert.ToInt32(2.5): " + Convert.ToInt32(2.5 * one) + "  Convert.ToInt32(3e9): " + Try(() => Convert.ToInt32(big)));
  }
}
