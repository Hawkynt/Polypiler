@EndToEnd @Layer.core
Feature: C# output for a concrete target framework

  Scenario Outline: The same algorithm compiles for and runs like every profile
    Given the fixture "wide/Crc32Table.cs"
    When I run "polypiler translate wide/Crc32Table.cs --from csharp --to csharp --profile <profile> --output out"
    Then the generated file compiles against the reference assemblies of <profile> at C# <langversion> without diagnostics
    And compiled for the running runtime, Compute(bytes of "123456789") returns 3421780262

    Examples:
      | profile        | langversion |
      | net2.0         | 2           |
      | net3.5         | 3           |
      | net4.6         | 6           |
      | net4.8         | 7.3         |
      | netstandard2.0 | 7.3         |
      | netstandard2.1 | 8           |
      | netcoreapp3.1  | 8           |
      | net8.0         | 12          |
      | net10.0        | 14          |

  Scenario: A rotate is a library call where the library has it and a helper where it does not
    Given the C# method "public static uint F(uint x, int n) => System.Numerics.BitOperations.RotateLeft(x, n);"
    When it is translated with "--profile netcoreapp3.1"
    Then the output calls "BitOperations.RotateLeft"
    When it is translated with "--profile net4.6 --disable-opt O0500"
    Then the output contains no "BitOperations" and declares a private rotate helper
    And both return 878082066 for F(0x12345678, 8)

  Scenario: The namespace form follows the language version
    Given any fixture translated with "--namespace Demo"
    Then the "net10.0" output contains "namespace Demo;"
    And the "net4.8" output contains "namespace Demo {"

  Scenario: A helper is emitted only when used
    Given the fixture "Adler32.cs" translated with "--profile net2.0"
    Then the generated class declares no helper method

  Scenario: An unknown profile is a usage error
    When I run the translate command with "--to csharp --profile net1.1"
    Then the exit code is 2 and the output contains "PLY0003"

  Scenario: The language version cannot be lowered below the profile's
    When I run the translate command with "--profile net10.0 --langversion 7.3"
    Then the exit code is 2 and the output contains "PLY0003"
