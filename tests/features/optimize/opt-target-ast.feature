@Layer.core
Feature: Backends build a target syntax tree, optimize it and print it last

  Both backends build a syntax tree of their target language, run the target passes over it and print
  it last (#1336). The passes share the optimization numbers of the IR rules: O0500 inlines a helper
  whose body is one expression, O0501 removes what nothing names any more.

  @Slow
  Scenario: The tree changes no output byte
    Given the recorded golden outputs of every fixture, profile, module format and float mode
    When each is translated again with "--opt none"
    Then every generated file is byte-identical to its golden output

  @Slow
  Scenario: Every printed file parses back to the tree it was printed from
    When every fixture is translated for every profile of both backends
    Then Roslyn at the profile's language version and Acornima at the profile's level parse each file without diagnostics
    And the parsed tree equals the printed target tree structurally

  Scenario: A single-expression helper is inlined
    Given the C# method "public static uint F(uint x) => System.Numerics.BitOperations.RotateLeft(x, 8);"
    When it is translated with "--to javascript --profile es5"
    Then the output contains no "function $rotl" and F(0x12345678) returns 878082066
    When it is translated with "--to javascript --profile es5 --disable-opt O0500"
    Then the output declares "$rotl" and calls it

  @Slow
  Scenario: A helper made unused by inlining is removed
    When every fixture is translated for every profile of both backends
    Then no generated file of any fixture declares a private helper that nothing references

  Scenario: An unknown target pass id is a usage error
    When I run the translate command with "--disable-opt O9998"
    Then the exit code is 2 and the output contains "PLY0003"
