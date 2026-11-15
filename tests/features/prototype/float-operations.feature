@EndToEnd @Layer.core
Feature: The complete floating-point operation set

  Scenario Outline: In strict mode every target returns the same bits
    Given the fixture "float/Dct8.cs"
    When it is translated to <to> with "--profile <profile> --float strict"
    Then Transform of the samples 1, 2, 3, 4, 5, 6, 7, 8 returns the eight bit patterns of the interpreter

    Examples:
      | to         | profile        |
      | csharp     | net2.0         |
      | csharp     | netstandard2.0 |
      | csharp     | net10.0        |
      | javascript | es5            |
      | javascript | es2022         |

  Scenario: In accurate mode a native sine is used and its difference is bounded and logged
    Given the C# method "public static double F(double x) => System.Math.Sin(x);"
    When it is translated with "--to javascript --profile es2022"
    Then the output calls "Math.sin" and its header names "Sin" with the bound of ADR 0005
    And for every reference vector the result is within that bound of the correctly rounded value
    And the sign of every zero result and every NaN and infinity matches exactly

  Scenario: Special values are exact everywhere
    Then Atan2(-0.0, -1.0) returns -π rounded, Atan2(-0.0, 1.0) returns -0.0, Hypot(∞, NaN) returns ∞, Pow(NaN, 0) returns 1, Log(-1) returns a NaN and Sqrt(-0.0) returns -0.0 in the interpreter and in every profile of both backends in both modes

  Scenario: JavaScript's power keeps its own special cases through C#
    Given the JavaScript function "export function f(x, y) { return Math.pow(x, y); }" with both parameters as numbers
    When it is translated to C#
    Then f(1, NaN) and f(-1, Infinity) return NaN, as in node, although Math.Pow returns 1

  Scenario: Single precision is not double precision rounded
    Given the C# method "public static float F(float x) => System.MathF.Exp(x);"
    Then in strict mode every profile of both backends returns the bits of the correctly rounded binary32 result for every reference vector the helper's contract covers

  Scenario: JavaScript rounding of a tie goes up
    Given the JavaScript function "export function f(x) { return Math.round(x); }"
    Then f(-2.5) returns -2 and f(2.5) returns 3 in node, in the interpreter and in every C# profile

  Scenario: Random numbers are refused
    Given the JavaScript function "export function f() { return Math.random(); }"
    When I run the translate command
    Then the exit code is 3 and the output contains "PLY1102" and "Math.random"
