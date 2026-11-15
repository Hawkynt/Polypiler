@EndToEnd
Feature: Exceptions and traps

  Scenario Outline: Every trap is caught by the clause of its class
    Given the fixture "exceptions/Traps.cs"
    When it is translated with "--to <to> --profile <profile>"
    Then Classify returns 1, 2, 3, 4, 5 and 0 for k = 0, 1, 2, 3, 4 and 5
    And Cast returns 6 for k = 0 and 5 for k = 1

    Examples:
      | to         | profile |
      | csharp     | net2.0  |
      | csharp     | net10.0 |
      | javascript | es5     |
      | javascript | es2015  |
      | javascript | es2022  |

  Scenario Outline: A trap that escapes keeps its JavaScript shape
    Given the C# method "public static int Div(int a, int b) => a / b;"
    When it is translated with "--to javascript --profile <profile>"
    Then Div(1, 0) throws an error e with e instanceof RangeError true, e.name "DivideByZeroError", e.message "divide by zero" and String(e) "DivideByZeroError: divide by zero"

    Examples:
      | profile |
      | es5     |
      | es2022  |

  Scenario: User exceptions keep message, inner exception and filters
    Given the fixture "exceptions/Classes.cs"
    When it is translated to JavaScript with "--profile es2022" and to C# with "--profile net2.0"
    Then Chain() returns "outer/cause"
    And Filter("x") returns 1 and Filter("y") returns 2
    And Fin() returns 1 and leaves the static field at 2
    And new X().Message is "Exception of type 'X' was thrown."

  Scenario: A filter with an effect runs before the inner finally, or is refused
    Given the fixture "exceptions/order.cs" whose filter logs "W" over an inner finally that logs "F" and whose handler logs "C"
    When it is translated with "--to csharp --profile net10.0"
    Then running it logs "W,F,C", as .NET does
    When it is translated with "--to csharp --profile net3.5" or "--to javascript"
    Then the exit code is 3 and the output contains "PLY1053" or "PLY1152" respectively and "runs before finally blocks"

  Scenario: JavaScript errors become C# exceptions
    Given the fixture "exceptions/errors.js" with "class X extends Error { constructor(m) { super(m); this.name = 'X'; } } export function f(k) { k = k | 0; try { if (k === 0) throw new X('x'); return BigInt.asIntN(64, 10n / BigInt((k - k) | 0)) > 0n ? 1 : 0; } catch (e) { if (e instanceof X) return 1; if (e instanceof RangeError) return 2; throw e; } }"
    When it is translated to C# with "--profile net10.0"
    Then f(0) returns 1 and f(1) returns 2, as in node

  Scenario: A thrown value that is not an Error is refused where it is read
    Given the JavaScript function "export function f() { try { throw 42; } catch (e) { return e + 1; } }"
    When I run the translate command with "--to csharp"
    Then the exit code is 3 and the output contains "PLY1107" and "not an Error"

  Scenario: return inside finally is refused
    Given the JavaScript function "export function f() { try { return 1; } finally { return 2; } }"
    When I run the translate command with "--to csharp"
    Then the exit code is 3 and the output contains "PLY1102" and "return in finally"
