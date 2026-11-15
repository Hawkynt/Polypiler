@EndToEnd @Layer.core
Feature: Emit the prototype IR as C#

  Scenario Outline: A fixture survives the round trip C# to IR to C#
    Given the fixture "<fixture>.cs"
    When I run "polypiler translate <fixture>.cs --from csharp --to csharp --output out"
    Then the exit code is 0 and "out/<class>.cs" exists
    And the generated file compiles with the C# compiler without warnings in both checked and unchecked project modes
    And calling "<call>" on the compiled result returns <expected>

    Examples:
      | fixture | class   | call                          | expected   |
      | Adler32 | Adler32 | Compute(bytes of "Wikipedia") | 300286872  |
      | Crc32   | Crc32   | Compute(bytes of "123456789") | 3421780262 |
      | Fnv1a   | Fnv1a   | Hash(bytes of "a")            | 3826002220 |
      | Numbers | Numbers | Gcd(1071, 462)                | 21         |
      | Numbers | Numbers | Fibonacci(48)                 | 512559680  |

  Scenario: Wrap-around does not depend on the project's overflow setting
    Given the C# method "public static int F(int a, int b) => a * b + 1;"
    When it is translated to C# and compiled with CheckOverflow enabled
    Then F(65536, 65536) returns 1 and throws nothing

  Scenario: Byte arithmetic is truncated where the source truncated it
    Given the C# method "public static byte F(byte a, byte b) => (byte)(a + b);"
    When it is translated to C# and compiled
    Then F(200, 100) returns 44

  Scenario: Traps become the matching .NET exceptions
    Given the C# method "public static int F(int[] d, int i, int b) => d[i] / b;"
    When it is translated to C# and compiled
    Then F([1], 0, 0) throws DivideByZeroException
    And F([1], 1, 1) throws IndexOutOfRangeException
    And F([-2147483648], 0, -1) throws OverflowException

  Scenario: A C# keyword used as an identifier is escaped
    Given an IR function with a local named "class"
    When it is emitted as C#
    Then the output declares "@class" and compiles

  Scenario: Ill-typed IR is refused
    Given a hand-built IR module that adds an I32 to a Bool
    When it is emitted as C#
    Then the emitter returns PLY0100 and no file

  Scenario: The namespace option wraps the class
    When I run the translate command with "--to csharp --namespace Demo.Checksums"
    Then the generated file starts its code with "namespace Demo.Checksums;"
