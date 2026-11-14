@EndToEnd
Feature: Translate wider C# to JavaScript

  Scenario Outline: A translated algorithm matches its published value
    Given the fixture "wide/<fixture>.cs"
    When it is translated to JavaScript
    Then node evaluates "<call>" on the generated module to <expected>
    And the interpreter and the compiled fixture return the same value

    Examples:
      | fixture     | call                                   | expected             |
      | Fnv1a64     | Hash(bytes of "a")                     | 12638187200555641996 |
      | Fnv1a64     | Hash(empty array)                      | 14695981039346656037 |
      | Crc16       | Compute(bytes of "123456789")          | 10673                |
      | Crc32Table  | Compute(bytes of "123456789")          | 3421780262           |
      | SplitMix64  | First(0)                               | 16294208416658607535 |

  Scenario: 64-bit arithmetic wraps
    Given the C# method "public static ulong F(ulong a) => a * 0x100000001B3UL + 1UL;"
    When it is translated and node calls F(18446744073709551615n)
    Then the result is 18446742974197923406n

  Scenario: Mixed uint and int widens to long as in C#
    Given the C# method "public static long F(uint a, int b) => a + b;"
    When it is translated and node calls F(4294967295, -1)
    Then the result is 4294967294n

  Scenario: Float arithmetic is rounded to binary32 after every operation
    Given the C# method "public static float F(float a, float b) => a * b + a;"
    When it is translated and node calls F(16777217, 3) with the arguments rounded to float
    Then the bit pattern of the result equals the compiled fixture's

  Scenario: continue still runs the for increment
    Given the C# method "public static int F(int n) { int s = 0; for (int i = 0; i < n; i++) { if ((i & 1) == 0) continue; s += i; } return s; }"
    When it is translated and node calls F(10)
    Then the result is 25

  Scenario: A switch selects by constant label without fall-through
    Given the fixture "wide/RunLength.cs"
    When it is translated
    Then node, the interpreter and the compiled fixture agree on every vector of the fixture

  Scenario: A checked addition traps
    Given the C# method "public static int F(int a, int b) => checked(a + b);"
    When it is translated and node calls F(2147483647, 1)
    Then the call throws a RangeError with the message "integer overflow"
    And the interpreter reports an integer-overflow trap

  Scenario: A library member outside the mapping table is refused
    Given the C# method "public static double F(double x) => System.Math.ReciprocalSqrtEstimate(x);"
    When it is translated
    Then the exit code is 3 and the output contains "PLY1002" and "Math.ReciprocalSqrtEstimate"
    And nothing is written

  Scenario: Writing to a constant table is refused
    Given a class with "static readonly uint[] T = { 1, 2 };" and a method assigning T[0]
    When it is translated
    Then the exit code is 3 and the output contains "PLY1002"
