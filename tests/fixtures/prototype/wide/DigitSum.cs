// Sums of decimal digits, each with a loop that runs its body before it tests its condition, because
// every number has at least one digit. Written for this repository.
namespace Polypiler.Fixtures.Prototype;

public static class DigitSum {

  public static int Sum(uint number) {
    int sum = 0;
    do {
      sum += (int)(number % 10);
      number /= 10;
    } while (number != 0);

    return sum;
  }

  public static int Count(ulong number) {
    int digits = 0;
    do {
      ++digits;
      number /= 10;
    } while (number != 0);

    return digits;
  }

  // The digital root: the digit sum of the digit sum, until one digit is left.
  public static int Root(uint number) {
    do {
      uint sum = 0;
      do {
        sum += number % 10;
        number /= 10;
      } while (number != 0);

      number = sum;
    } while (number >= 10);

    return (int)number;
  }

  // The sum of the even digits only: the odd ones end their pass early.
  public static int SumOfEven(long number) {
    if (number < 0)
      number = -number;

    int sum = 0;
    do {
      long digit = number % 10;
      number /= 10;
      if ((digit & 1) != 0)
        continue;

      sum += (int)digit;
    } while (number > 0);

    return sum;
  }

}
