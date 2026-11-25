public static class Kernel {
  public static int Run() {
    var n = 6;
    var sum = 0;
    while (n > 0) {
      sum += n * 2;
      n -= 1;
    }
    return sum;
  }
}
