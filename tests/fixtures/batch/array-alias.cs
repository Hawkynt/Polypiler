public static class Kernel {
  public static int Run() {
    int[] numbers = [10, 20, 30];
    var alias = numbers;
    var other = new int[3];
    int index = 1;
    int before = alias[index];
    numbers[index + 1] = 42;
    other[index + 1] = 7;
    return before + alias[index + 1] + other[index + 1] + other[0] + numbers.Length;
  }
}
