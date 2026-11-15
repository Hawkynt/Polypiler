@EndToEnd @Layer.core
Feature: Spelled-out bit idioms become the target's own operations
  The pass pipeline runs between the frontend and the backend: recognition rules turn rotates,
  counts, byte swaps, bit tests, min/max/abs/sign/clamp and endian loads and stores written out by
  hand into the operations of the IR, which the backends write as the target's own calls. Every rule
  is switched off by its optimization number, and a rule that cannot show its result exact does not
  apply.

  Scenario: A rotate written as two shifts becomes the library call
    Given the C# method "public static uint F(uint x) => (x << 8) | (x >> 24);"
    When it is translated with "--to csharp --profile net10.0"
    Then the output calls "BitOperations.RotateLeft" and contains no "<< 8"
    And F(0x12345678) returns 878082066
    When it is translated with "--to csharp --profile net10.0 --disable-opt O0100"
    Then the output contains "<< 8" and no "RotateLeft"

  Scenario: Byte loads joined by shifts become one endian load
    Given the C# method "public static uint F(byte[] b, int i) => (uint)(b[i] | b[i + 1] << 8 | b[i + 2] << 16 | b[i + 3] << 24);"
    When it is translated with "--to csharp --profile net10.0"
    Then the output calls "BinaryPrimitives.ReadUInt32LittleEndian"
    And F(bytes 78 56 34 12, 0) returns 0x12345678

  Scenario: A spelled-out xxHash32 recovers its rotates and loads
    Given the fixture "recognize/XxHash32Spelled.cs"
    When it is translated with "--to javascript --profile es2022 --explain-opt"
    Then standard error lists O0100 at least 5 times and O0140 at least once
    And Hash(empty, 0) returns 0x02CC5D05 with and without "--opt none"

  Scenario: A branchless absolute value does not become a trapping one
    Given the C# method "public static int F(int x) { int m = x >> 31; return (x ^ m) - m; }"
    Then the optimized IR contains "AbsWrap" and no "Abs("
    And F(-2147483648) returns -2147483648 in every profile

  Scenario: A near-miss is left alone
    Given the C# method "public static uint F(uint x) => (x << 8) | (x >> 23);"
    Then no rule is applied and the output contains "<< 8" and ">> 23"

  Scenario: An unknown optimization id is a usage error
    When I run the translate command with "--disable-opt O9999x"
    Then the exit code is 2 and the output contains "PLY0003"
