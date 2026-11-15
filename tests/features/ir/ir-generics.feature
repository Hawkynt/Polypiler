@EndToEnd @Layer.core
Feature: Generic methods and classes

  Scenario Outline: A generic search keeps .NET's equality in every profile
    Given the fixture "generics/Search.cs"
    When it is translated with "--to <to> --profile <profile>"
    Then IndexOf of [1.0, NaN] and NaN returns 1
    And IndexOf of [-0.0] and 0.0 returns 0
    And IndexOf of [3, 5, 7] and 7 returns 2, and of [3, 5, 7] and 4 returns -1

    Examples:
      | to         | profile |
      | csharp     | net2.0  |
      | csharp     | net10.0 |
      | javascript | es5     |
      | javascript | es2015  |
      | javascript | es2022  |

  Scenario: C# gets one generic definition back
    Given the fixture "generics/Search.cs"
    When it is translated with "--to csharp --profile net2.0"
    Then the output declares "IndexOf<T>(T[] a, T v) where T : global::System.IEquatable<T>" once and no method "IndexOf_Double"

  Scenario Outline: A generic class over a 64-bit and a floating-point type
    Given the fixture "generics/Ring.cs"
    When it is translated with "--to javascript --profile <profile>"
    Then a Ring<long> of capacity 3 after pushing 1, 2, 3 and 9007199254740993 has Last 9007199254740993 and Count 4
    And a Ring<double> of capacity 2 after pushing 0.5 and -0.0 has a Last whose reciprocal is negative infinity
    And the output declares the classes "Ring$i64" and "Ring$f64"

    Examples:
      | profile |
      | es5     |
      | es2020  |

  # The module class of middle.js is named middle, which no member of it can be in C#: the overloads are middle_.
  Scenario: A JavaScript helper used with two array types becomes two C# overloads
    Given the fixture "generics/middle.js"
    When it is translated with "--to csharp --profile net10.0"
    Then the output declares "middle_(int[] a)" and "middle_(double[] a)"
    And f() returns -3.75, as in node

  Scenario: default and new() follow the type argument
    Given the fixture "generics/Search.cs"
    When it is translated with "--to javascript --profile es2022"
    Then Def<int> returns 0, Def<double> returns 0, Def<bool> returns false and Def<Box> of the fixture's class Box returns null
    And Make<Box>() returns a new Box each call

  Scenario: Polymorphic recursion is refused
    Given the fixture "generics/Recursion.cs" with "static int Depth<T>(int n) => n == 0 ? 0 : 1 + Depth<T[]>(n - 1);"
    When I run the translate command with "--to javascript"
    Then the exit code is 3 and the output contains "PLY1002" and "the instantiations of 'Depth<T>' do not end"
    And no file is written

  Scenario: An exported JavaScript function still has one type per parameter
    Given the JavaScript module "export function first(a) { return a[0]; } export function g() { return first(new Int32Array(1)) + first(new Float64Array(1)); }"
    When I run the translate command with "--to csharp"
    Then the exit code is 3 and the output contains "PLY1105"
