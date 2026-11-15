@EndToEnd @Layer.core
Feature: Resource scopes

  Scenario Outline: Resources are disposed in reverse order in every profile
    Given the fixture "resources/Scopes.cs"
    When it is translated with "--to <to> --profile <profile>"
    Then Nest() logs 1, 2, 5, -2, -1

    Examples:
      | to         | profile        |
      | csharp     | net2.0         |
      | csharp     | netstandard2.0 |
      | csharp     | netstandard2.1 |
      | csharp     | net10.0        |
      | javascript | es5            |
      | javascript | es2015         |
      | javascript | es2022         |

  Scenario Outline: A using declaration keeps its form only where C# has it
    Given the fixture "resources/Scopes.cs"
    When it is translated with "--to csharp --profile <profile>"
    Then the method Declarations, which Nest calls, <has> "using var"

    Examples:
      | profile        | has              |
      | netstandard2.0 | does not contain |
      | netstandard2.1 | contains         |

  Scenario: The body's exception leaves after disposal
    Given the fixture "resources/Scopes.cs"
    When it is translated with "--to javascript --profile es2022"
    Then BodyThrows() logs 1, 5, -1 and the caller catches an X with the message "body"

  Scenario: An exception from Dispose replaces the body's exception
    Given the fixture "resources/Scopes.cs"
    When it is translated with "--to javascript --profile es5" and with "--to csharp --profile net2.0"
    Then DisposeThrows() logs 1, 5, -1 and the caller catches an X with the message "dispose 1"
    And BothThrow() logs 1, 2, 5, -2, -1 and the caller catches an X with the message "dispose 1"

  Scenario: A null resource is not disposed
    Given the fixture "resources/Scopes.cs"
    When it is translated with "--to javascript --profile es2022"
    Then NullResource() logs 5 and throws nothing

  Scenario: JavaScript's try/finally disposal reaches C# unchanged
    Given the fixture "resources/scopes.js"
    When it is translated to C# with "--profile net10.0"
    Then bothThrow() logs 1, 2, 5, -2, -1 and the caller catches an X with the message "dispose 1", as in node
    And the output contains "finally" and no "using ("

  Scenario: await using is refused
    Given the C# method "public static async System.Threading.Tasks.Task F(System.IAsyncDisposable r) { await using (r) { } }" in "resources/AsyncScope.cs"
    When I run the translate command with "--to javascript"
    Then the exit code is 3 and the output contains "PLY1002" and "await using"
