public static class Kernel {
  public static int Run() {
    var value = int.MaxValue;
    return unchecked(value + 1);
  }
}
