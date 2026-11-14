@EndToEnd
Feature: Strings of UTF-16 code units

  Scenario Outline: A hash over the code units of a string agrees in every profile
    Given the fixture "<fixture>"
    When it is translated with "--to <to> --profile <profile>"
    Then Hash("hello") returns 1335831723 and Hash("h\u00E9llo\U0001F600") returns 4113148722

    Examples:
      | fixture               | to         | profile        |
      | strings/Fnv1aText.cs  | javascript | es5            |
      | strings/Fnv1aText.cs  | javascript | es2022         |
      | strings/fnv1a-text.js | csharp     | net2.0         |
      | strings/fnv1a-text.js | csharp     | netstandard2.0 |
      | strings/fnv1a-text.js | csharp     | net10.0        |

  Scenario: C#'s Replace replaces every occurrence, JavaScript's replace only the first
    Given the C# method "public static string F(string s) => s.Replace(\"b\", \"XY\");"
    And the JavaScript function "export function g(s) { return s.replace('b', 'XY'); }"
    When both are translated to the other language in every profile
    Then F("abcb") returns "aXYcXY" and g("abcb") returns "aXYcb" in every profile, as in .NET and node

  Scenario: Each language keeps its own white-space set
    Then the translation of C#'s s.Trim() returns a string of length 3 for "\uFEFFa\uFEFF" and of length 1 for "\u0085a\u0085"
    And the translation of JavaScript's s.trim() returns a string of length 1 for "\uFEFFa\uFEFF" and of length 3 for "\u0085a\u0085"

  Scenario: JavaScript iterates a string by code points, C# by code units
    Given the JavaScript function "export function f(s) { let n = 0; for (const ch of s) n++; return n; }"
    And the C# method "public static int G(string s) { int n = 0; foreach (char c in s) n++; return n; }"
    When both are translated to the other language with the default profile and the oldest profile
    Then f("a\u{1F600}") returns 2 and G("a\U0001F600") returns 3 in every translation

  Scenario: Ordinal comparison of strings
    Given the JavaScript function "/** @param {string} a @param {string} b */ export function f(a, b) { return a < b; }"
    When it is translated to C#
    Then f("a", "B") returns false, as in node, and the output contains "string.CompareOrdinal"

  Scenario: A culture-sensitive C# member is refused
    Given the C# method "public static bool F(string s) => s.StartsWith(\"e\");"
    When I run the translate command to JavaScript
    Then the exit code is 3 and the output contains "PLY1002", "StartsWith(string)" and "StringComparison.Ordinal"

  Scenario: Case mapping is refused in JavaScript
    Given the JavaScript function "export function f(s) { return s.toUpperCase(); }"
    When I run the translate command to C#
    Then the exit code is 3 and the output contains "PLY1102" and "toUpperCase"
