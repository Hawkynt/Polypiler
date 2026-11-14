@EndToEnd
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
