@EndToEnd
Feature: Emit the widened IR as C#

  Scenario Outline: A wide fixture survives the round trip C# to IR to C#
    Given the fixture "wide/<fixture>.cs"
    When I run "polypiler translate wide/<fixture>.cs --from csharp --to csharp --output out"
    Then the generated file compiles without warnings in both checked and unchecked project modes
    And calling "<call>" on the compiled result returns <expected>

    Examples:
      | fixture    | call                          | expected             |
      | Fnv1a64    | Hash(bytes of "a")            | 12638187200555641996 |
      | Crc16      | Compute(bytes of "123456789") | 10673                |
      | Crc32Table | Compute(bytes of "123456789") | 3421780262           |
      | SplitMix64 | First(0)                      | 16294208416658607535 |

  Scenario: A JavaScript checksum becomes C#
    Given the fixture "js/adler32.mjs"
    When I run "polypiler translate js/adler32.mjs --from javascript --to csharp --output out"
    Then the generated C# compiles and compute(bytes of "Wikipedia") returns 300286872

  Scenario: A constant table is emitted once as a read-only field
    Given the fixture "wide/Crc32Table.cs"
    When it is translated to C#
    Then the output declares exactly one "private static readonly uint[]" field with 256 elements
    And no method assigns to an element of it

  Scenario: A checked addition stays checked whatever the project says
    Given the C# method "public static int F(int a, int b) => checked(a + b);"
    When it is translated to C# and compiled with CheckOverflow disabled
    Then F(2147483647, 1) throws OverflowException

  Scenario: Float arithmetic is not widened
    Given the C# method "public static float F(float a, float b) => a * b + a;"
    When it is translated to C# and compiled
    Then the bit pattern of F(16777217f, 3f) equals the original fixture's

  Scenario: A for loop with continue keeps its step
    Given the C# method "public static int F(int n) { int s = 0; for (int i = 0; i < n; i++) { if ((i & 1) == 0) continue; s += i; } return s; }"
    When it is translated to C# and compiled
    Then F(10) returns 25

  Scenario: An IR node the emitter does not know is refused
    Given an IR module containing a node kind unknown to the C# backend
    When it is checked
    Then the result is PLY1052 and nothing is emitted
