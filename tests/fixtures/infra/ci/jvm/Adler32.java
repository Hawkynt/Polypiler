// Adler-32 of the ASCII bytes "abc", or of no bytes with the argument --empty, as RFC 1950 §8.2
// computes it: prints 38600999, or 1.
public final class Adler32 {

  public static void main(String[] args) {
    byte[] data = args.length > 0 && args[0].equals("--empty") ? new byte[0] : new byte[] { 'a', 'b', 'c' };
    long a = 1;
    long b = 0;
    for (byte value : data) {
      a = (a + (value & 0xff)) % 65521;
      b = (b + a) % 65521;
    }
    System.out.println((b << 16) | a);
  }

}
