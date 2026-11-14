@EndToEnd
Feature: Translate a small C# algorithm to JavaScript

  Scenario Outline: A translated checksum matches its published value
    Given the fixture "<fixture>.cs"
    When I run "polypiler translate <fixture>.cs --from csharp --to javascript --output out"
    Then the exit code is 0 and "out/<class>.mjs" exists
    And node evaluates "<call>" on the generated module to <expected>
    And the same call on the fixture compiled by the C# compiler returns <expected>

    Examples:
      | fixture | class   | call                                              | expected   |
      | Adler32 | Adler32 | Compute(bytes of "Wikipedia")                     | 300286872  |
      | Crc32   | Crc32   | Compute(bytes of "123456789")                     | 3421780262 |
      | Fnv1a   | Fnv1a   | Hash(empty array)                                 | 2166136261 |
      | Fnv1a   | Fnv1a   | Hash(bytes of "a")                                | 3826002220 |
      | Numbers | Numbers | Gcd(1071, 462)                                    | 21         |
      | Numbers | Numbers | Fibonacci(48)                                     | 512559680  |
      | Numbers | Numbers | RotateLeft(0x80000001, 1)                         | 3          |

  Scenario: Signed arithmetic wraps like C#
    Given the C# method "public static int F(int a, int b) => a * b + 1;"
    When it is translated and node calls F(65536, 65536)
    Then the result is 1
    And the interpreter returns 1 for the same call

  Scenario: An array is modified in place
    Given the fixture "Arrays.cs" with "public static void Reverse(int[] data)"
    When it is translated and node calls Reverse on [1, 2, 3, 4, 5]
    Then the array is [5, 4, 3, 2, 1]

  Scenario: Division by zero fails the same way in both
    Given the C# method "public static int F(int a, int b) => a / b;"
    When it is translated and node calls F(1, 0)
    Then the call throws a RangeError with the message "divide by zero"
    And the interpreter reports a divide-by-zero trap for the same call

  Scenario: An unsupported construct is refused and nothing is written
    Given the C# method "public static decimal F(decimal a) => a + 1;"
    When I run the translate command on it
    Then the exit code is 3 and the output contains "PLY1002" with line 1
    And the output directory contains no file

  Scenario: Invalid C# is reported with the compiler's message
    Given the C# text "public static int F( => 1;"
    When I run the translate command on it
    Then the exit code is 4 and the output contains "PLY1000"

  Scenario: An unknown target language is a usage error
    When I run "polypiler translate Adler32.cs --from csharp --to cobol --output out"
    Then the exit code is 2 and the output contains "PLY0003"
