@EndToEnd @Layer.core
Feature: async and await between Task and Promise

  Scenario Outline: A sequence of awaited calls runs alike everywhere
    Given the fixture "async/Pipeline.cs"
    When it is translated with "--to <to> --profile <profile>"
    Then awaiting Run(4) returns 22 and awaiting Run(0) returns 0

    Examples:
      | to         | profile        |
      | javascript | es2015         |
      | javascript | es2017         |
      | javascript | es2022         |
      | csharp     | net4.6         |
      | csharp     | netstandard2.0 |
      | csharp     | net10.0        |

  Scenario: The same in the other direction
    Given the fixture "async/pipeline.mjs"
    When it is translated with "--to csharp --profile net4.8"
    Then awaiting Run(4) returns 22, as in node

  Scenario: Independent pure calls fan out with WhenAll and Promise.all
    Then Squares() returns [4, 9] in .NET, in node and in every admitted profile of both backends

  Scenario: The first fault in argument order wins
    Given the fixture "async/Pipeline.cs"
    When it is translated with "--to javascript --profile es2022"
    Then Faults() returns "A" in .NET and in the generated JavaScript, although Promise.all alone rejects with "B"

  Scenario: A task that is not awaited where it is created is refused
    Given the C# method "static async Task<int> F() { await Task.CompletedTask; return 1; } static void Log(int x) { } public static async Task<int> M() { var t = F(); Log(2); return await t; }"
    When I run the translate command to JavaScript
    Then the exit code is 3 and the output contains "PLY1002" and "not awaited where it is created"

  Scenario Outline: Profiles without a faithful form are refused
    Given the fixture "async/Pipeline.cs"
    When it is translated with "--to <to> --profile <profile>"
    Then the exit code is 3 and the output contains "<code>"

    Examples:
      | to         | profile | code    |
      | javascript | es5     | PLY1152 |
      | csharp     | net3.5  | PLY1053 |
      | csharp     | net2.0  | PLY1053 |

  Scenario: The orders of .NET and node are measured anew
    Then every ordering probe gives its documented trace in .NET and in node
