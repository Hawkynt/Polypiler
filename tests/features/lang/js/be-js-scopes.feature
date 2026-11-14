@EndToEnd
Feature: Shadowed locals keep their meaning in generated JavaScript
  A let hides every other variable of its name from its whole block, the reads in front of it and its
  own initializer included; the IR does not. Above ECMAScript 5 the JavaScript backend renames a local
  declared while one of its name is visible to <name>_<k>, the rule of the C# backend; ECMAScript 5
  keeps its var renaming.

  Scenario: An inner local that shadows an outer one read in the same block
    Given the IR function f(n: i32) with t: f64 = 0.5 and an inner block declaring t': i32 = n * 2 and returning t' + (t + t)
    When it is emitted for every JavaScript profile
    Then f(3) returns 7 in node for every profile

  Scenario Outline: Every class of shadowing computes what the interpreter and C# compute
    Given the IR case "<case>"
    When it is emitted for every JavaScript profile
    Then every call returns in node for every profile what the interpreter and the C# compiler's build return

    Examples:
      | case                    |
      | inner after outer       |
      | outer read before inner |
      | parameter shadowed      |
      | loop variable shadowed  |
      | suffix taken            |
      | siblings                |

  Scenario: Sibling blocks keep their names
    Given the IR case "siblings"
    When it is emitted for every JavaScript profile
    Then the generated JavaScript declares t in both blocks and renames neither
