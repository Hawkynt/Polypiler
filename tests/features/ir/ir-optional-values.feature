@EndToEnd @Layer.core
Feature: Optional values and null

  Scenario Outline: A nullable search result reads the same in every profile
    Given the fixture "optional/Find.cs"
    When it is translated with "--to <to> --profile <profile>"
    Then Find([4, 5, 6], 6) ?? -1 returns 2 and Find([4, 5, 6], 7) ?? -1 returns -1
    And with int? a = null, (a + 1) ?? -1 returns -1, a < 1 returns false and a == null returns true
    And with int? z = 0, z ?? 5 returns 0
    And with int? q = null, q ??= 7 then q ??= 9 leaves q at 7

    Examples:
      | to         | profile |
      | csharp     | net2.0  |
      | csharp     | net4.6  |
      | csharp     | net10.0 |
      | javascript | es5     |
      | javascript | es2020  |
      | javascript | es2022  |

  Scenario: The null-conditional operator on an array
    Given the C# method "public static int L(int[] a) => a?.Length ?? -1;"
    When it is translated with "--to javascript --profile es5"
    Then L(null) returns -1 and L(new int[3]) returns 3
    And the output contains neither "?." nor "??"

  Scenario: JavaScript's coercion of null survives the way to C#
    Given the JavaScript function "export function f(x) { const v = x > 0 ? x : null; return v + 1; }"
    When it is translated to C# with "--profile net10.0"
    Then f(-3) returns 1 and f(2) returns 3, as in node

  Scenario: Unwrapping none traps
    Given the C# method "public static int V() { int? e = null; return e.Value; }"
    When it is translated to JavaScript and to C# with every profile
    Then V() throws in every output, in C# a System.InvalidOperationException with the message "Nullable object must have a value."

  Scenario: An exported JavaScript function keeps its absent value
    Given the JavaScript function "export function g(n) { if (n > 0) return n; }"
    When it is translated to C# and to JavaScript with "--profile es2022"
    Then g(0) returns undefined, not null, as in node, and the C# method returns null, the one absent value of C#

  Scenario: Two absent values the program tells apart are refused
    Given the fixture "optional/absent.js" with "export function f(x) { return x === undefined ? 1 : x === null ? 2 : x; }"
    When I run the translate command with "--to csharp"
    Then the exit code is 3 and the output contains "PLY1105" and "holds both null and undefined"
