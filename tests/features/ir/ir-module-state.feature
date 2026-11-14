@EndToEnd @Layer.core
Feature: Mutable module state

  Scenario Outline: A generator keeps its state between calls in every target
    Given the fixture "<fixture>"
    When it is translated with "--to <to> --profile <profile> <module>"
    Then three calls of Next return 723471715, 2497366906 and 2064144800, and Calls then returns 3

    Examples:
      | fixture             | to         | profile | module                     |
      | state/Xorshift32.cs | javascript | es5     | --module cjs               |
      | state/Xorshift32.cs | javascript | es5     | --module iife --global Rng |
      | state/Xorshift32.cs | javascript | es2022  |                            |
      | state/xorshift32.js | csharp     | net2.0  |                            |
      | state/xorshift32.js | csharp     | net10.0 |                            |

  Scenario Outline: 64-bit state survives the replacement of BigInt
    Given the fixture "state/Xorshift64.cs"
    When it is translated with "--to javascript --profile <profile>"
    Then three calls of Next return 8748534153485358512, 3040900993826735515 and 3453997556048239312

    Examples:
      | profile          |
      | es5 --module cjs |
      | es2017           |
      | es2020           |

  Scenario: The elements of an array global change, the array does not
    Given the fixture "state/histogram.js"
    When it is translated to C# with "--profile net4.8"
    Then add(1), add(5) and add(2) return 1, 2 and 1

  Scenario: A public mutable static field is refused
    Given the C# class "public static class G { public static int Counter; public static int F() => ++Counter; }"
    When I run the translate command to JavaScript
    Then the exit code is 3 and the output contains "PLY1002" and "Counter"

  Scenario: An exported variable is refused
    Given the JavaScript module "export let counter = 0; export function f() { return ++counter; }"
    When I run the translate command to C#
    Then the exit code is 3 and the output contains "PLY1102" and "counter"
