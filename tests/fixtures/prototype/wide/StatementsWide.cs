// One small function per statement form beyond those of Statements.cs: do, switch, break, continue,
// foreach, the steps as expressions and local constants, each with the loop counts and branches the
// tests need. Written for this repository.
namespace Polypiler.Fixtures.Prototype;

public static class StatementsWide {

  // continue in a for still runs the increment; with two incrementors both run.
  public static int SumOfOdd(int n) {
    int sum = 0;
    for (int i = 0; i < n; i++) {
      if ((i & 1) == 0)
        continue;

      sum += i;
    }

    return sum;
  }

  public static int TwoCounters(int n) {
    int result = 0;
    for (int up = 0, down = n; up < down; ++up, --down) {
      if (up % 3 == 0)
        continue;

      result += down - up;
    }

    return result;
  }

  // The step writes to an array element whose index is more than a variable.
  public static int StepThroughArray(int[] counters, int n) {
    int passes = 0;
    for (int i = 0; i < n; counters[i % counters.Length] += 1, ++i) {
      if (i == 2)
        continue;

      ++passes;
    }

    return passes;
  }

  public static int BreakAt(int n, int stop) {
    int i = 0;
    while (i < n) {
      if (i == stop)
        break;

      ++i;
    }

    return i;
  }

  // break and continue belong to the innermost loop only.
  public static int Nested(int rows, int columns) {
    int cells = 0;
    for (int row = 0; row < rows; ++row) {
      if (row == 1)
        continue;

      int column = 0;
      while (true) {
        if (column >= columns)
          break;

        ++column;
        if (column == 2)
          continue;

        ++cells;
      }

      if (row == 3)
        break;
    }

    return cells;
  }

  public static int DoOnce(int n) {
    int passes = 0;
    do {
      ++passes;
      if (passes == 5)
        break;
    } while (passes < n);

    return passes;
  }

  public static int DoContinue(int n) {
    int i = 0;
    int skipped = 0;
    do {
      ++i;
      if (i % 2 == 0)
        continue;

      ++skipped;
    } while (i < n);

    return skipped;
  }

  // Grouped labels, the default in the middle, a section that ends with return.
  public static int Classify(int value) {
    int kind;
    switch (value) {
      case 0:
        kind = 10;
        break;
      case 1:
      case 2:
      case 3:
        kind = 20;
        break;
      default:
        kind = -1;
        break;
      case -1:
      case int.MinValue:
        return 30;
      case int.MaxValue:
        kind = 40;
        break;
    }

    return kind;
  }

  // No label matches and there is no default: the switch does nothing.
  public static int NoDefault(int value) {
    int result = 7;
    switch (value) {
      case 1:
        result = 1;
        break;
      case 2:
        result = 2;
        break;
    }

    return result;
  }

  public static int Vowels(char[] text) {
    int count = 0;
    foreach (char letter in text) {
      switch (letter) {
        case 'a':
        case 'e':
        case 'i':
        case 'o':
        case 'u':
          ++count;
          break;
        case 'y':
          continue;
        case 'z':
          return -1;
      }
    }

    return count;
  }

  public static int SwitchLong(long value) {
    switch (value) {
      case long.MinValue:
        return 1;
      case -1L:
        return 2;
      case 0x100000000L:
        return 3;
      default:
        return 0;
    }
  }

  public static int SwitchULong(ulong value) {
    switch (value) {
      case ulong.MaxValue:
        return 1;
      case 0:
        return 2;
    }

    return 0;
  }

  public static int SwitchByte(byte value) {
    switch (value) {
      case 255:
        return 1;
      case 0:
        return 2;
      default:
        return value;
    }
  }

  public static int SwitchSByte(sbyte value) {
    switch (value) {
      case -128:
        return 1;
      case 127:
        return 2;
      default:
        return value;
    }
  }

  // A local declared in one section is in scope in the others.
  public static int SharedLocal(int value) {
    switch (value) {
      case 0:
        int shared = 5;
        return shared;
      case 1:
        shared = 6;
        return shared + 1;
      default:
        int other;
        other = value;
        return other;
    }
  }

  // A switch in a loop: break leaves the switch, continue goes on with the loop.
  public static int SwitchInLoop(int n) {
    int total = 0;
    for (int i = 0; i < n; ++i) {
      switch (i % 4) {
        case 0:
          continue;
        case 1:
          total += 1;
          break;
        case 2:
          total += 10;
          break;
        default:
          total += 100;
          break;
      }

      total += 1000;
    }

    return total;
  }

  public static long SumEach(long[] values) {
    long sum = 0;
    foreach (long value in values)
      sum += value;

    return sum;
  }

  public static double SumEachDouble(float[] values) {
    double sum = 0;
    foreach (double value in values)
      sum += value;

    return sum;
  }

  // The iteration variable is narrower than the element: the cast keeps the low bits.
  public static int SumEachNarrowed(int[] values) {
    int sum = 0;
    foreach (byte value in values)
      sum += value;

    return sum;
  }

  public static int SumEachChecked(int[] values) {
    int sum = 0;
    checked {
      foreach (byte value in values)
        sum += value;
    }

    return sum;
  }

  public static int FirstNegative(sbyte[] values) {
    int index = -1;
    foreach (var value in values) {
      ++index;
      if (value >= 0)
        continue;

      return index;
    }

    return -1;
  }

  public static int CountUntilZero(ushort[] values) {
    int count = 0;
    foreach (ushort value in values) {
      if (value == 0)
        break;

      ++count;
    }

    return count;
  }

  private static int[] Same(int[] values) => values;

  // The array expression of a foreach is evaluated once.
  public static int SumOfCall(int[] values) {
    int sum = 0;
    foreach (int value in Same(values))
      sum += value;

    return sum;
  }

  // Steps as expressions: the value before for the postfix form, the value after for the prefix form.
  public static int PostIncrement(int a) => a++ + a;
  public static int PreIncrement(int a) => ++a + a;
  public static int PostDecrement(int a) => a-- - a;
  public static int PreDecrement(int a) => --a - a;
  public static uint PostIncrementUInt(uint a) => a++ + a;
  public static long PostIncrementLong(long a) => a++ + a;
  public static ulong PreDecrementULong(ulong a) => --a + a;
  public static int PostIncrementByte(byte a) => a++ + a;
  public static int PreDecrementSByte(sbyte a) => --a + a;
  public static int PostDecrementShort(short a) => a-- + a;
  public static int PreIncrementUShort(ushort a) => ++a + a;
  public static int PostIncrementChar(char a) => a++ + a;
  public static float PostIncrementFloat(float a) => a++ + a;
  public static double PreDecrementDouble(double a) => --a + a;
  public static int PostIncrementChecked(int a) => checked(a++ + 1);
  public static int PreDecrementCheckedByte(byte a) => checked(--a + 1);
  public static long PreIncrementCheckedLong(long a) => checked(++a);
  public static double PostIncrementCheckedDouble(double a) => checked(a++ + a);

  public static int StepElement(int[] data, int index) => data[index]++ + ++data[index];
  public static long StepLongElement(long[] data, int index) => data[index]-- + --data[index];
  public static int StepByteElement(byte[] data, int index) => --data[index] + data[index]++;
  public static float StepFloatElement(float[] data, int index) => data[index]++ + ++data[index];
  public static int StepCheckedElement(sbyte[] data, int index) => checked(data[index]++ + ++data[index]);
  public static ulong StepCheckedULongElement(ulong[] data, int index) => checked(--data[index]);

  // Left to right: the index of the target is read before the step on the right moves it.
  public static void CopyBackwards(int[] source, int[] target) {
    int from = source.Length;
    int to = 0;
    while (from > 0)
      target[to++] = source[--from];
  }

  public static int StepInCondition(int n) {
    int passes = 0;
    while (n-- > 0)
      ++passes;

    return passes * 1000 + n;
  }

  public static int StepAsArgument(int a) => Twice(a++) + Twice(++a) + a;

  private static int Twice(int value) => value * 2;

  // Calls nested in expressions, evaluated left to right.
  public static int NestedCalls(int a, int b) => Twice(Twice(a) + Twice(b)) - Twice(Twice(Twice(b)));

  // Local constants of several types; the compiler folds every use.
  public static long Constants(int a) {
    const int Scale = 3;
    const long Offset = 1L << 40;
    const uint Mask = 0xFFFF;
    const double Half = 0.5;
    const char Letter = 'A';
    const bool Enabled = true;
    return Enabled ? (a & Mask) * Scale + Offset + (long)(Half * 4) + Letter : 0;
  }

  public static double ChooseDouble(bool first, double a, double b) => first ? a : b;
  public static long[] ChooseArray(bool first, long[] a, long[] b) => first ? a : b;

}
