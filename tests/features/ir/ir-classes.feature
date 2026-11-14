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

  Scenario: References alias, records compare by value
    Given the fixtures "classes/Counter.cs" and "classes/Range.cs"
    Then in .NET, the interpreter, every C# profile and node at es5 and es2022: after b = a and b.Next() on new Counter(0), a.Count is 1, a and b are the same object, and two new Counter(0) are not
    And new Range(2, 9) equals new Range(2, 9) by value but not by reference, and its Length is 7

  Scenario: A method called on null traps in every target
    Given the C# method "public static int F() { Counter c = null; return c.Next(); }" next to the class Counter
    Then .NET throws NullReferenceException, the interpreter traps with NullReference, every C# profile throws NullReferenceException and node at es5 and es2022 throws a TypeError
