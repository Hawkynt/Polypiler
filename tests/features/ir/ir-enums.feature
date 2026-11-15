@EndToEnd
Feature: Enums as named integer types

  Scenario Outline: Flag names follow .NET in every JavaScript profile
    Given the fixture "enums/Perm.cs"
    When it is translated with "--to javascript --profile <profile>"
    Then Describe(0) returns "None", Describe(3) returns "ReadWrite", Describe(5) returns "Read, Exec", Describe(7) returns "ReadWrite, Exec" and Describe(8) returns "8"

    Examples:
      | profile          |
      | es5 --module cjs |
      | es2015           |
      | es2022           |

  Scenario: An enum wraps in its underlying type and keeps undefined values
    Given the fixture "enums/Color.cs"
    When it is translated to JavaScript and to C# with "--profile net2.0"
    Then Shift(Color.Blue, 250) returns "Red", Cast(7) returns "7" and IsDefined(6) returns true in both

  Scenario: Names are listed by unsigned value
    Given the fixture "enums/Signed.cs"
    When it is translated to JavaScript with "--profile es2022"
    Then Names() returns "A,C,B,Neg"

  Scenario Outline: A JavaScript enum object becomes a C# enum
    Given the fixture "enums/color.js"
    When it is translated with "--to csharp --profile <profile>"
    Then the output contains "public enum Color : int" and shade(5) returns 20, shade(6) returns 30 and shade(7) returns 30

    Examples:
      | profile        |
      | net2.0         |
      | netstandard2.0 |
      | net10.0        |

  Scenario: An unknown name traps as in .NET
    Given the C# method "public static int F(string s) => (int)(Perm)System.Enum.Parse(typeof(Perm), s);" of "enums/Perm.cs"
    When it is translated to JavaScript
    Then F(" Read ") returns 1, F("Read, Write") returns 3 and F("read") traps with "unknown name"

  Scenario: An enum object with a computed key is refused
    Given the JavaScript module "const C = Object.freeze({ A: 0, B: 1 }); export function f(k) { return C[k]; }"
    When I run the translate command to C#
    Then the exit code is 3 and the output contains "PLY1102" and "C"
