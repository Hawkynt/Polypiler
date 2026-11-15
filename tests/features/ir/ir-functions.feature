@EndToEnd @Layer.core
Feature: Delegates, lambdas, local functions and closures

  Scenario Outline: A C# for loop shares its variable, a foreach makes one per pass
    Given the fixture "functions/Closures.cs"
    When it is translated with "--to javascript --profile <profile>"
    Then in node ForCapture() returns 9 and ForeachCapture([0, 1, 2]) returns 3
    And the output parses at the level of <profile>

    Examples:
      | profile |
      | es5     |
      | es2015  |
      | es2022  |

  Scenario Outline: A JavaScript let makes one variable per pass, also when the body assigns it
    Given the fixture "functions/closures.js"
    When it is translated with "--to csharp --profile <profile>"
    Then compiled for the running runtime forVar() returns 9, forLet() returns 3 and forLetAssigned() returns 9
    And the generated file compiles against the reference assemblies of <profile> without diagnostics

    Examples:
      | profile        |
      | net2.0         |
      | net3.5         |
      | netstandard2.0 |
      | net10.0        |

  Scenario: Closures share the variable they capture, in both directions
    Given the fixtures "functions/Closures.cs" and "functions/closures.js"
    When each is translated to the other language with the default profile
    Then Counter() returns 123, Interleaved() returns 21, Late(2) returns 10, Fact(5) returns 120, Fact(0) returns 1 and Composed(4) returns 15 in .NET, in node and in both generated files
    And MethodGroup(-7) of the C# fixture returns 49 in the generated JavaScript

  Scenario: Each profile writes the form it has
    Given the fixture "functions/Closures.cs"
    When it is translated to C# with "--profile net2.0"
    Then the output contains "delegate (" and a nested delegate type "Int32ToInt32" and no "=>" and no "System.Func"
    When it is translated to C# with "--profile net3.5"
    Then the output contains "System.Func<int, int>" and "=>" and no local function
    When it is translated to C# with "--profile net10.0"
    Then Fact is written with a local function and every lambda without captures is "static"
    When it is translated to JavaScript with "--profile es5"
    Then the output contains no "=>", no "let" and no "const"

  Scenario: Calling a null function value traps in both directions
    Given the fixture "functions/Closures.cs" and its method CallNull
    When it is translated to JavaScript and CallNull() is called in node
    Then it throws as the NullReference trap of #1323, where .NET throws System.NullReferenceException
    And the JavaScript function callNull() of "functions/closures.js", which throws TypeError "f is not a function" in node, translated to C# throws System.NullReferenceException

  Scenario: A multicast delegate is refused
    Given the C# method "public static int F() { System.Func<int> f = () => 1; f += () => 2; return f(); }"
    When I run the translate command to JavaScript
    Then the exit code is 3 and the output contains "PLY1002" and "multicast delegate" and nothing is written

  Scenario: JavaScript's arguments object is refused
    Given the JavaScript function "export function f(a) { const g = function () { return arguments.length; }; return g(a, a); }"
    When I run the translate command to C#
    Then the exit code is 3 and the output contains "PLY1102" and "arguments" and nothing is written
