@EndToEnd
Feature: Exceptions and traps

  Scenario Outline: A trap that escapes keeps its JavaScript shape
    Given the C# method "public static int Div(int a, int b) => a / b;"
    When it is translated with "--to javascript --profile <profile>"
    Then Div(1, 0) throws an error e with e instanceof RangeError true, e.name "DivideByZeroError", e.message "divide by zero" and String(e) "DivideByZeroError: divide by zero"

    Examples:
      | profile |
      | es5     |
      | es2022  |
