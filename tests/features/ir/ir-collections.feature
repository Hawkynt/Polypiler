@EndToEnd
Feature: Lists, maps and sets

  Scenario Outline: Each language's enumeration order survives the translation
    Given the fixtures "collections/Collections.cs" and "collections/collections.js"
    When the C# fixture is translated with "--to javascript --profile <js>" and the JavaScript fixture with "--to csharp --profile <cs>"
    Then DictionaryOrder() returns 5246 and SetOrder() returns 143 in .NET and in the generated JavaScript
    And mapOrder() returns 2456 and setOrder() returns 134 in node and in the generated C#

    Examples:
      | js     | cs             |
      | es5    | net2.0         |
      | es2015 | net3.5         |
      | es2022 | netstandard2.0 |
      | es2022 | net8.0         |
      | es2022 | net10.0        |

  Scenario: Lists and growable arrays keep their contents
    Given the fixtures "collections/Collections.cs" and "collections/collections.js"
    When each is translated to the other language with the default profile
    Then ListOps() returns 7233 and Count([3, 1, 3, 2, 3]) returns 331121 in .NET and in the generated JavaScript
    And arrayOps() returns 72334 and count([3, 1, 3, 2, 3]) returns 331121 in node and in the generated C#

  Scenario Outline: Changing a collection while enumerating it behaves as in the source language
    Given the fixtures "collections/Collections.cs" and "collections/collections.js"
    When the C# fixture is translated with "--to javascript --profile <js>" and the JavaScript fixture with "--to csharp --profile <cs>"
    Then ModifyWhileEnumerating() throws the CollectionModified trap in the generated JavaScript, as .NET throws System.InvalidOperationException
    And RemoveWhileEnumerating() returns 22 in .NET and in the generated JavaScript
    And pushWhileIterating() returns 4 in node and in the generated C#

    Examples:
      | js     | cs      |
      | es5    | net2.0  |
      | es2022 | net10.0 |

  Scenario: Float keys keep each language's equality and kept key
    Given the fixtures "collections/Collections.cs" and "collections/collections.js"
    When each is translated to the other language with the default profile
    Then FirstZeroKey() returns negative infinity and NaNKey() returns 12 in .NET and in the generated JavaScript
    And firstZeroKey() returns positive infinity and nanKey() returns 12 in node and in the generated C#

  Scenario: A missing key traps in C# and is undefined in JavaScript
    Given the fixtures "collections/Collections.cs" and "collections/collections.js"
    When each is translated to the other language with the default profile
    Then MissingKey() throws the KeyNotFound trap in the generated JavaScript, as .NET throws System.Collections.Generic.KeyNotFoundException
    And missing() returns an empty optional in the generated C#, as node returns undefined

  Scenario: A sorted dictionary is refused
    Given the C# method "public static int F() { var d = new System.Collections.Generic.SortedDictionary<int, int>(); d[1] = 1; return d.Count; }"
    When I run the translate command to JavaScript
    Then the exit code is 3 and the output contains "PLY1002" and "SortedDictionary" and nothing is written

  Scenario: A Map keyed by object literals is refused
    Given the JavaScript function "export function f() { const m = new Map(); m.set({ x: 1 }, 1); return m.size; }"
    When I run the translate command to C#
    Then the exit code is 3 and the output contains "PLY1102" and "keyed by objects of an object literal" and nothing is written
