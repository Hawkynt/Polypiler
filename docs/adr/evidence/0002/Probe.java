public class Probe {
  interface F { Object get(); }
  static String t(F f) { try { return String.valueOf(f.get()); } catch (Throwable e) { return e.getClass().getSimpleName(); } }
  public static void main(String[] a) {
    int max = Integer.MAX_VALUE, min = Integer.MIN_VALUE, one = 1, zero = 0, m1 = -1;
    System.out.println("add: " + (max + one) + "  sub: " + (min - one) + "  mul: " + (65536 * (one * 65536)) + "  addExact: " + t(() -> Math.addExact(max, one)));
    System.out.println("1 << 32: " + (one << 32) + "  1 << 33: " + (one << 33) + "  1 << -1: " + (one << m1) + "  -8 >> 33: " + (-8 >> 33) + "  -8 >>> 33: " + (-8 >>> 33));
    System.out.println("7 / 0: " + t(() -> 7 / zero) + "  7 % 0: " + t(() -> 7 % zero));
    System.out.println("MIN / -1: " + (min / m1) + "  MIN % -1: " + (min % m1));
    System.out.println("-7 / 3: " + (-7 / (one * 3)) + "  -7 % 3: " + (-7 % (one * 3)) + "  7 % -3: " + (7 % (m1 * 3)) + "  floorMod(-7,3): " + Math.floorMod(-7, 3));
    System.out.println("(int)3e9: " + (int) (3e9 * one) + "  (int)-3e9: " + (int) (-3e9 * one) + "  (int)NaN: " + (int) (Double.NaN * one) + "  (int)Inf: " + (int) (one / 0.0) + "  (byte)300: " + (byte) (300 * one));
  }
}
