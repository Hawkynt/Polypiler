Feature: Value records

  Scenario Outline: A struct stays a copy in every JavaScript profile
    Given the fixture "records/Geometry.cs"
    When it is translated with "--to javascript --profile <profile>"
    Then Copy() returns 161 in node, as in .NET
    And ArrayCopy() returns 397 in node, as in .NET

    Examples:
      | profile |
      | es5     |
      | es2015  |
      | es2022  |

  Scenario Outline: Record-struct equality compiles and holds in every C# profile
    Given the fixture "records/Money.cs"
    When it is translated with "--to csharp --profile <profile>"
    Then the generated file compiles against the reference assemblies of <profile> at its language version without diagnostics
    And Same(150, 150) returns true and Same(150, 151) returns false
    And NaNEqual() returns true, because P2(NaN, 0.0) equals P2(NaN, -0.0)
    And the declaration of Money is "<form>"

    Examples:
      | profile        | form                                                   |
      | net2.0         | public struct Money : global::System.IEquatable<Money> |
      | netstandard2.0 | public struct Money : global::System.IEquatable<Money> |
      | net10.0        | public record struct Money(long Cents, int Currency)   |

  Scenario: JavaScript object literals become C# structs
    Given the fixture "records/geometry.js"
    When it is translated with "--to csharp --profile net10.0"
    Then the output declares "public readonly struct Point"
    And midX(1, 2, 4, 6) returns 2.5 and dotOf(1, 2, 3, 4) returns 11, as in node

  Scenario: A 128-bit addition on a struct of two halves agrees everywhere
    Given the fixture "records/U128.cs"
    Then Add of (Lo 0xFFFFFFFFFFFFFFFF, Hi 0) and (Lo 1, Hi 0) returns Lo 0 and Hi 1 in .NET, in the interpreter, in every C# profile and in node at es5, es2020 and es2022

  Scenario: An object written through a second name is refused
    Given the JavaScript function "export function f() { const p = { x: 1, y: 2 }; const q = p; q.x = 5; return p.x; }"
    When I run the translate command to C#
    Then the exit code is 3
    And the output contains "PLY1102" and "an object literal written after it is built"

  Scenario: Comparing object literals by identity is refused
    Given the JavaScript function "export function f(a) { const p = { x: a }; const q = { x: a }; return p === q; }"
    When I run the translate command to C#
    Then the exit code is 3 and the output contains "PLY1102" and "==="
