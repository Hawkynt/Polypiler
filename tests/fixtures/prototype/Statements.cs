// One small function per statement kind of the subset, each with the loop counts and branches the
// tests need. Written for this repository.
namespace Polypiler.Fixtures.Prototype;

public static class Statements {

  public static int Sign(int value) {
    if (value < 0)
      return -1;
    else if (value == 0)
      return 0;
    else
      return 1;
  }

  public static int Clamp(int value, int low, int high) {
    if (value < low) {
      value = low;
    }

    if (value > high) {
      value = high;
    } else {
      { int unused = value; value = unused; }
    }

    return value;
  }

  public static int SumTo(int count) {
    int sum = 0;
    for (int i = 1; i <= count; ++i)
      sum += i;

    return sum;
  }

  public static int Halvings(int value) {
    int steps = 0;
    while (value > 0) {
      value /= 2;
      steps++;
    }

    return steps;
  }

  public static int IndexOf(byte[] data, byte wanted) {
    for (int i = 0; i < data.Length; i++) {
      if (data[i] == wanted)
        return i;
    }

    return -1;
  }

  public static int IntegerRoot(int square) {
    for (int root = 0; ; ++root)
      if (root * root >= square)
        return root;
  }

  public static int Triangle(int size) {
    int cells = 0;
    int row;
    for (row = 0; row < size; row++) {
      int column = 0;
      while (column <= row) {
        ++cells;
        column += 1;
      }
    }

    return cells + row;
  }

  public static int Factorial(int n) => n <= 1 ? 1 : n * Factorial(n - 1);

  public static bool IsEven(int n) => n == 0 || IsOdd(n - 1);

  private static bool IsOdd(int n) => n != 0 && IsEven(n - 1);

  public static int Declarations(int seed) {
    int a = seed, b = a + 1;
    var c = a * b;
    uint d;
    d = (uint)c;
    bool big = d > 1000;
    byte low = (byte)d;
    return big ? low : -low;
  }

  // The right operand of && and || runs only when it decides the result; the log shows whether it ran.
  public static bool Both(bool first, int[] log) => first && Mark(log, 0);

  public static bool Either(bool first, int[] log) => first || Mark(log, 0);

  public static bool Neither(bool first, bool second) => !(first || second) == (!first && !second) && first != second;

  // Operands are evaluated left to right: 1 - 2 * 3.
  public static int Order(int[] log) => Count(log) - Count(log) * Count(log);

  public static void Touch(int[] log) => Mark(log, 1);

  public static void Fill(int[] log, int value) {
    for (int i = 0; i < log.Length; ++i)
      log[i] = value;

    return;
  }

  private static bool Mark(int[] log, int slot) {
    log[slot] = 1;
    return true;
  }

  private static int Count(int[] log) {
    log[0] += 1;
    return log[0];
  }

}
