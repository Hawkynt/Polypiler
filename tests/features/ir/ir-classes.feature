@EndToEnd @Layer.core
Feature: Sealed classes and instances

  Scenario Outline: A C# class becomes a JavaScript class or constructor function
    Given the fixture "classes/Counter.cs"
    When it is translated with "--to javascript --profile <profile>"
    Then the output contains "<form>"
    And in node: new Counter(10), Step = 3, Next() twice returns 16 and Count is 16, as in .NET

    Examples:
      | profile | form                     |
      | es5     | Counter.prototype.Next = |
      | es2015  | class Counter {          |
      | es2022  | #count                   |

  Scenario Outline: A JavaScript class with private state compiles in every C# profile
    Given the fixture "classes/counter.js"
    When it is translated with "--to csharp --profile <profile>"
    Then the generated file compiles against the reference assemblies of <profile> at its language version without diagnostics
    And new Counter(10), step = 3, next() twice returns 16, count is 16 and Counter.created is 1, as in node

    Examples:
      | profile        |
      | net2.0         |
      | netstandard2.0 |
      | net10.0        |

  Scenario: An ES5 constructor function with prototype methods becomes a C# class
    Given the fixture "classes/accumulator.js"
    When it is translated with "--to csharp"
    Then the output declares "public sealed class Acc"
    And new Acc(1), add(2), add(4) returns 7, as in node

  Scenario: References alias, records compare by value
    Given the fixtures "classes/Counter.cs" and "classes/Range.cs"
    Then in .NET, the interpreter, every C# profile and node at es5 and es2022: after b = a and b.Next() on new Counter(0), a.Count is 1, a and b are the same object, and two new Counter(0) are not
    And new Range(2, 9) equals new Range(2, 9) by value but not by reference, and its Length is 7

  Scenario: Adler-32 as an object
    Given the fixtures "classes/Adler32.cs" and "classes/adler32.js"
    Then Update with the bytes of "Wikipedia" followed by Value returns 300286872 in .NET, node, the interpreter and every profile of both backends in both directions

  Scenario: A method called on null traps in every target
    Given the C# method "public static int F() { Counter c = null; return c.Next(); }" next to the class Counter
    Then .NET throws NullReferenceException, the interpreter traps with NullReference, every C# profile throws NullReferenceException and node at es5 and es2022 throws a TypeError

  Scenario: A property added outside the constructor is refused
    Given the JavaScript module "export class P { constructor() { this.a = 1; } } export function f() { const p = new P(); p.b = 2; return p.a; }"
    When I run the translate command to C#
    Then the exit code is 3 and the output contains "PLY1102" and "a property added outside the constructor"
