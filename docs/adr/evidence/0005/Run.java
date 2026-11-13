// Evaluates the ADR 0005 input set with java.lang.Math (default) or java.lang.StrictMath (argument "strict").
import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.io.PrintStream;

public final class Run {
  public static void main(String[] args) throws Exception {
    boolean strict = args.length > 0 && args[0].equals("strict");
    BufferedReader in = new BufferedReader(new InputStreamReader(System.in));
    PrintStream out = new PrintStream(new java.io.BufferedOutputStream(System.out, 1 << 16));
    String line;
    while ((line = in.readLine()) != null) {
      String[] parts = line.split(" ");
      double[] v = new double[3];
      for (int i = 2; i < parts.length; ++i)
        v[i - 2] = Double.longBitsToDouble(Long.parseUnsignedLong(parts[i], 16));
      double x = v[0], y = v[1], z = v[2], r;
      switch (parts[0]) {
        case "sin": r = strict ? StrictMath.sin(x) : Math.sin(x); break;
        case "cos": r = strict ? StrictMath.cos(x) : Math.cos(x); break;
        case "tan": r = strict ? StrictMath.tan(x) : Math.tan(x); break;
        case "exp": r = strict ? StrictMath.exp(x) : Math.exp(x); break;
        case "log": r = strict ? StrictMath.log(x) : Math.log(x); break;
        case "log2": out.println("n/a"); continue;
        case "log10": r = strict ? StrictMath.log10(x) : Math.log10(x); break;
        case "pow": r = strict ? StrictMath.pow(x, y) : Math.pow(x, y); break;
        case "atan2": r = strict ? StrictMath.atan2(x, y) : Math.atan2(x, y); break;
        case "sinh": r = strict ? StrictMath.sinh(x) : Math.sinh(x); break;
        case "cosh": r = strict ? StrictMath.cosh(x) : Math.cosh(x); break;
        case "tanh": r = strict ? StrictMath.tanh(x) : Math.tanh(x); break;
        case "cbrt": r = strict ? StrictMath.cbrt(x) : Math.cbrt(x); break;
        case "hypot": r = strict ? StrictMath.hypot(x, y) : Math.hypot(x, y); break;
        case "add": r = x + y; break;
        case "sub": r = x - y; break;
        case "mul": r = x * y; break;
        case "div": r = x / y; break;
        case "sqrt": r = strict ? StrictMath.sqrt(x) : Math.sqrt(x); break;
        case "fma": r = strict ? StrictMath.fma(x, y, z) : Math.fma(x, y, z); break;
        default: throw new IllegalArgumentException(parts[0]);
      }
      // doubleToRawLongBits keeps the NaN pattern the operation produced
      out.println(String.format("%016x", Double.doubleToRawLongBits(r)));
    }
    out.flush();
  }
}
