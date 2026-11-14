@EndToEnd
Feature: JavaScript output for a concrete target

  Scenario Outline: The same algorithm runs at every language level
    Given the fixture "wide/Fnv1a64.cs"
    When I run "polypiler translate wide/Fnv1a64.cs --from csharp --to javascript --profile <profile> --module <module> <extra> --output out"
    Then the generated file parses at ECMAScript level <level>
    And node evaluates Hash(bytes of "a") on it to the 64-bit value 12638187200555641996

    Examples:
      | profile | module | extra          | level |
      | es5     | cjs    |                | 5     |
      | es5     | iife   | --global Fnv   | 5     |
      | es2015  | esm    |                | 2015  |
      | es2017  | cjs    |                | 2017  |
      | es2020  | esm    |                | 2020  |
      | es2022  | esm    |                | 2022  |

  Scenario: Below ES2020 a 64-bit result is a pair of halves
    Given the fixture "wide/Fnv1a64.cs" translated with "--profile es2015"
    When node calls Hash(bytes of "a")
    Then the result is the object { hi: 2942557260, lo: 2248273036 }
    And the file contains no "BigInt"

  Scenario: Math.imul is replaced where it does not exist
    Given the C# method "public static int F(int a, int b) => a * b;"
    When it is translated with "--profile es5 --module cjs"
    Then the output contains no "Math.imul"
    And node returns 1 for F(2147483647, 2147483647)

  Scenario: Float arithmetic stays binary32 without Math.fround
    Given the C# method "public static float F(float a, float b) => a * b + a;"
    When it is translated with "--profile es5 --module cjs"
    Then the bit pattern node computes for F(16777217, 3) equals the compiled fixture's

  Scenario: An ES module is not available at ES5
    When I run the translate command with "--profile es5 --module esm"
    Then the exit code is 2 and the output contains "PLY0001"

  Scenario: The profile is recorded in the output
    Given any fixture translated with "--profile es2017 --module cjs"
    Then the header comment of the generated file names "es2017" and "cjs"
