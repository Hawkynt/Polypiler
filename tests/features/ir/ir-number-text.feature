@EndToEnd @Layer.core
Feature: Numbers to text and back, by the rules of the source language

  Scenario Outline: C# formatting survives translation to JavaScript
    Given the C# method "public static string F(double x) => \"v=\" + x.ToString(System.Globalization.CultureInfo.InvariantCulture);"
    When it is translated with "--to javascript --profile <profile>"
    Then F(1e21) returns "v=1E+21", F(1.5e-6) returns "v=1.5E-06", F(1e16) returns "v=10000000000000000", F(1e17) returns "v=1E+17" and F(-0.0) returns "v=-0"

    Examples:
      | profile |
      | es5     |
      | es2015  |
      | es2022  |

  Scenario Outline: JavaScript formatting survives translation to C#
    Given the JavaScript function "export function f(x) { return 'v=' + x; }"
    When it is translated with "--to csharp --profile <profile>"
    Then f(1e21) returns "v=1e+21", f(1e20) returns "v=100000000000000000000", f(1.5e-6) returns "v=0.0000015" and f(-0) returns "v=0"

    Examples:
      | profile        |
      | net2.0         |
      | netstandard2.0 |
      | netcoreapp3.1  |
      | net10.0        |

  Scenario: Fixed notation keeps each language's tie rule
    Given the C# method "public static string F(double x) => x.ToString(\"F2\", System.Globalization.CultureInfo.InvariantCulture);"
    And the JavaScript function "export function g(x) { return x.toFixed(2); }"
    When both are translated to the other language in every profile
    Then F(0.125) returns "0.12", F(-0.0) returns "-0.00", g(0.125) returns "0.13" and g(-0) returns "0.00"

  Scenario: Parsing keeps each language's grammar
    Given the JavaScript function "export function f(s) { return Number(s); }"
    And the C# method "public static double G(string s) => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture);"
    When both are translated to the other language in every profile
    Then f("0x10") returns 16, f("") returns 0 and f("1,000") returns NaN
    And G("1,000") returns 1000, G("infinity") returns Infinity and G("0x10") traps with "invalid format"

  Scenario: Truth values keep their spelling
    Given the C# method "public static string F(bool b) => \"b=\" + b;"
    And the JavaScript function "/** @param {boolean} b */ export function g(b) { return 'b=' + b; }"
    When both are translated to the other language in every profile
    Then F(true) returns "b=True" and g(true) returns "b=true"

  Scenario: Formatting without a culture is announced
    Given the C# method "public static string F(double x) => \"a\" + x;"
    When it is translated with "--to javascript --profile es2022"
    Then the generated header names the caveat "cs-invariant-culture" and F(2.5) returns "a2.5"

  Scenario: A culture-specific format is refused
    Given the C# method "public static string F(double x) => x.ToString(System.Globalization.CultureInfo.GetCultureInfo(\"de-DE\"));"
    When I run the translate command to JavaScript
    Then the exit code is 3 and the output contains "PLY1002" and "CultureInfo.GetCultureInfo"
