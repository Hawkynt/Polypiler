// 32-bit int arithmetic of the Java Language Specification: addition wraps (§15.18.2), the count of a
// shift is masked with 31 (§15.19). Without arguments it prints Integer.MAX_VALUE + 1 and -1 >>> 28;
// each argument "add:<n>" prints Integer.MAX_VALUE + n, each "ushr:<n>" prints -1 >>> n.
public final class Wrap {

  public static void main(String[] args) {
    if (args.length == 0) {
      System.out.println(Integer.MAX_VALUE + 1);
      System.out.println(-1 >>> 28);
      return;
    }

    for (String argument : args) {
      int n = Integer.parseInt(argument.substring(argument.indexOf(':') + 1));
      System.out.println(argument.startsWith("add:") ? Integer.MAX_VALUE + n : -1 >>> n);
    }
  }

}
