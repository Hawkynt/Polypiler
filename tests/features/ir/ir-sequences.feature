@EndToEnd @Layer.core
Feature: Iterators and generators

  Scenario Outline: Laziness and finally on early exit survive the translation
    Given the fixture "<fixture>"
    When it is translated with "--to <to> --profile <profile>"
    Then EarlyBreak returns 57189 and Full returns 718283896 in the generated code, as in the source language

    Examples:
      | fixture                  | to         | profile        |
      | sequences/Iterators.cs   | javascript | es5            |
      | sequences/Iterators.cs   | javascript | es2015         |
      | sequences/Iterators.cs   | javascript | es2022         |
      | sequences/iterators.js   | csharp     | net2.0         |
      | sequences/iterators.js   | csharp     | netstandard2.0 |
      | sequences/iterators.js   | csharp     | net10.0        |

  Scenario: A C# sequence restarts, a JavaScript generator object does not
    Given the fixtures "sequences/Iterators.cs" and "sequences/iterators.js"
    When each is translated to the other language with the default profile
    Then Twice() of the C# fixture returns 6 in .NET and in the generated JavaScript
    And twice() of the JavaScript fixture returns 3 in node and in the generated C#

  Scenario Outline: Infinite and empty sequences with int32 wrap-around
    Given the fixture "sequences/Iterators.cs"
    When it is translated with "--to javascript --profile <profile>"
    Then FibSum(10) returns 88, FibSum(0) returns 0, FibSum(47) returns 512559679, Guard(-1) returns 0, Guard(0) returns 1, Throws() returns 14 and Nested(3) returns 34

    Examples:
      | profile |
      | es5     |
      | es2022  |

  Scenario: ES5 output uses no generator syntax and still runs
    Given the fixture "sequences/iterators.js"
    When it is translated with "--to javascript --profile es5 --module cjs"
    Then the output contains no "function*", no "yield", no "for (" with "of" and no "Symbol"
    And it parses at ECMAScript 5 and earlyBreak() returns 57189 and fibSum(47) returns 512559679 in node

  Scenario: A yield that receives a value is refused
    Given the JavaScript function "export function f() { function* g() { const x = yield 1; } for (const v of g()) {} return 0; }"
    When I run the translate command to C#
    Then the exit code is 3 and the output contains "PLY1102" and "yield as a value" and nothing is written

  Scenario: Manual enumeration is refused
    Given the C# method "public static int F(System.Collections.Generic.IEnumerable<int> s) { var e = s.GetEnumerator(); e.MoveNext(); return e.Current; }"
    When I run the translate command to JavaScript
    Then the exit code is 3 and the output contains "PLY1002" and "GetEnumerator" and nothing is written
