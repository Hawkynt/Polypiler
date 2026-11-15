// Compiles cleanly and throws at run time: the JVM prints the stack trace and exits with 1.
public final class Throws {

  public static void main(String[] args) {
    throw new IllegalStateException("thrown on purpose");
  }

}
