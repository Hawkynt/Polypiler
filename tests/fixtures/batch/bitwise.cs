public static class Kernel {
  public static int Run() {
    int x = int.MinValue + 1, count = 33;
    int left = x << count, signed = x >> count, unsigned = x >>> count;
    count = -32;
    return (left | (signed & ~unsigned)) ^ (x >>> count);
  }
}
