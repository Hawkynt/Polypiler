@EndToEnd @Layer.core
Feature: Generated C# is immune to the JIT's shift-xor rotate defect
  .NET's JIT compiles an exclusive or of a left and a logical right shift of one value by n and w - n
  to a rotation, also at n = 0, where C# computes x ^ x = 0 and the rotation gives x. The C# backend
  writes the complement count so that the JIT computes what C# defines, and leaves every expression
  the JIT computes correctly as it is (docs/reference/known-runtime-defects.md).

  Scenario: A shift-xor with a variable count computes C#'s result at count 0
    Given the C# method "public static uint F(uint x, int n) => (x << n) ^ (x >> (32 - n));"
    When it is translated with "--to csharp --opt none" and the output is compiled in Release and executed on .NET 10
    Then F(0x12345678, 0) returns 0 and the result equals the interpreter for every n in 0..33

  Scenario: The unaffected form is not changed
    Given the C# method "public static uint F(uint x) => (x << 8) | (x >> 24);"
    Then the generated expression is unchanged by this issue
