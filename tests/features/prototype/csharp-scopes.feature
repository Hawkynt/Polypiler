Feature: Locals of one name in nested C# scopes
  C# forbids a local whose name a local of an enclosing block or a parameter already has, whichever
  of the two comes first in the text (CS0136); JavaScript and the IR allow it. The C# backend renames
  the later of two such locals to <name>_<k> and leaves every other name as it is.

  Scenario Outline: A block-local let followed by an outer let of the same name compiles
    Given the fixture "js/scopes/<file>"
    When I run "polypiler translate js/scopes/<file> --from javascript --to csharp --profile <profile> --output out"
    Then the exit code is 0
    And the generated file compiles against the reference assemblies of <profile> without diagnostics
    And <call> returns <value>, as node returns for the source

    Examples:
      | file     | profile | call | value |
      | later.js | net2.0  | f(3) | 3.5   |
      | later.js | net10.0 | f(0) | 0.5   |
      | loop.js  | net4.8  | g(4) | 16    |
      | loop.js  | net10.0 | g(0) | 10    |

  Scenario: A local nested in a local of the same name and another type is renamed
    Given the IR function F(n: i32): f64 declaring t: f64 = 0.5 and, inside if (n > 0), t: i32 = n * 2
    When it is emitted as C# for net10.0
    Then the inner local is written as "t_1"
    And the file compiles, and F(3) returns 7 and F(-1) returns 1, as the interpreter and node return

  Scenario: Sibling blocks keep their names
    Given the fixture "Sibling.cs" with two sibling blocks each declaring t, one int and one double
    When it is translated to C# for net10.0
    Then the output contains two declarations named "t" and no "t_1"
