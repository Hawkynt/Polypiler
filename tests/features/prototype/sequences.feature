@EndToEnd @Layer.core
Feature: LINQ and JavaScript array callbacks keep their evaluation order

  Scenario Outline: Deferred execution survives the translation to JavaScript
    Given the fixture "sequences/Pipelines.cs"
    When it is translated with "--to javascript --profile <profile>"
    Then Deferred() returns 312124 in the generated code, as in .NET
    And Trace() returns [1,-10,2,-20,3,-30,4,-40] in the generated code, as in .NET

    Examples:
      | profile |
      | es5     |
      | es2015  |
      | es2022  |

  Scenario Outline: An eager chain stays eager in C#
    Given the fixture "sequences/pipes.mjs"
    When it is translated with "--to csharp --profile <profile>"
    Then trace([1,2,3]) returns [1,2,3,-2,-4,-6] in the generated code, as in node
    And total([1,2,3]) returns 10 in the generated code, as in node
    And eager() returns 1111222234 in the generated code, as in node

    Examples:
      | profile        |
      | net2.0         |
      | net3.5         |
      | netstandard2.0 |
      | net10.0        |

  Scenario Outline: Sum is checked for integers and accumulated in double for float
    Given the fixture "sequences/Pipelines.cs"
    When it is translated with "--to javascript --profile <profile>"
    Then SumOf([2147483647,1]) returns -1 in the generated code, as in .NET
    And SumOf([2147483647,-1,1]) returns 2147483647 in the generated code, as in .NET
    And SumOfSingles([16777216,1,1]) returns 16777218 in the generated code, as in .NET

    Examples:
      | profile |
      | es5     |
      | es2015  |
      | es2017  |
      | es2020  |
      | es2022  |

  Scenario Outline: OrderBy is stable at every language level
    Given the fixture "sequences/Pipelines.cs"
    When it is translated with "--to <to> --profile <profile>"
    Then Stable([2,1,2,1],[10,20,30,40]) returns [20,40,10,30] in the generated code, as in .NET
    And the output defines the stable merge sort "<helper>"

    Examples:
      | to         | profile | helper      |
      | javascript | es5     | $stableSort |
      | javascript | es2015  | $stableSort |
      | javascript | es2022  | $stableSort |
      | csharp     | net2.0  | _stableSort |
      | csharp     | net10.0 | _stableSort |

  Scenario Outline: NaN is found by includes but not by indexOf, in both directions
    Given the fixture "<fixture>"
    When it is translated with "--to <to> --profile <profile>"
    Then <call> returns <value> in the generated code, as in <oracle>

    Examples:
      | fixture                | to         | profile | call          | value | oracle |
      | sequences/pipes.mjs    | csharp     | net2.0  | nanSearch()   | 9     | node   |
      | sequences/pipes.mjs    | csharp     | net10.0 | nanSearch()   | 9     | node   |
      | sequences/Pipelines.cs | javascript | es5     | IndexOfNaN()  | 0     | .NET   |
      | sequences/Pipelines.cs | javascript | es2022  | IndexOfNaN()  | 0     | .NET   |
      | sequences/Pipelines.cs | javascript | es2022  | ContainsNaN() | 1     | .NET   |

  Scenario Outline: First of an empty sequence throws an InvalidOperationException
    Given the fixture "sequences/Pipelines.cs"
    When it is translated with "--to javascript --profile <profile>"
    Then FirstOf([]) returns -1 in the generated code, as in .NET
    And FirstOf([]) returns -1 in the interpreter

    Examples:
      | profile |
      | es5     |
      | es2022  |

  Scenario: A lazy First stops before the trap that an eager map reaches
    Given the fixtures "sequences/Pipelines.cs" and "sequences/pipes.mjs" translated to each other's language
    Then LazyTrap() of the C# fixture returns 5 in the generated JavaScript, as in .NET
    And eagerThrow() of the JavaScript fixture returns 3 in the generated C#, as in node

  Scenario: Constructs whose order is not defined are refused
    Given the JavaScript function "export function f() { let n = 0; const a = [2, 1]; a.sort((x, y) => { n++; return x - y; }); return n; }"
    When I run the translate command to C#
    Then the exit code is 3 and the output contains "PLY1102" and "sort comparator with effects" and nothing is written

  Scenario: Ordering strings by the culture is refused
    Given the C# method "public static string F(string[] a) => System.Linq.Enumerable.First(System.Linq.Enumerable.OrderBy(a, s => s));"
    When I run the translate command to JavaScript
    Then the exit code is 3 and the output contains "PLY1002" and "culture-sensitive comparison" and nothing is written
