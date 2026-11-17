@Layer.core
Feature: --assume contracts

  A caller contract is checked at the boundary or reported, never silently trusted. The tree IR of
  ADR 0009 has no Dyn operations: the scenarios run on modules the C# frontend imports, whose
  parameters carry their proven types, and the IR interpreter is the oracle.

  Scenario: A checked assumption guards the boundary
    Given the C# methods "public static int Sum(int n) { int s = 0; for (int i = 0; i < n; i++) s += i; return s; }"
    And the assumption "Sum.n: int[0, 65535]" in checked mode
    When the contracts are enforced
    Then the entry of "Sum" starts with the check of "Sum.n: int[0, 65535]"
    And the assumption is listed with checked = true
    And interpreting "Sum" with 4 returns 6

  Scenario: A violating call fails before any effect
    Given the C# methods "static int calls; public static int F(int n) { calls++; return n; } public static int Calls() => calls;"
    And the assumption "F.n: int[0, 9]" in checked mode
    When the contracts are enforced
    Then interpreting "F" with 10 raises ContractViolation naming "F.n: int[0, 9]"
    And interpreting "Calls" returns 0

  Scenario: An unchecked assumption is reported, not hidden
    Given the C# methods "public static int Sum(int n) { int s = 0; for (int i = 0; i < n; i++) s += i; return s; }"
    And the assumption "Sum.n: int[0, 65535]" given with --assume-unchecked
    When the contracts are enforced
    Then warning PLY0512 is reported for "Sum.n: int[0, 65535]"
    And the entry of "Sum" has no check
    And the assumption is listed with checked = false

  Scenario: An assumption that contradicts the program is an error
    Given the C# methods "public static int Show(string x) => x.Length;"
    And the assumption "Show.x: i32" in checked mode
    When the contracts are enforced
    Then error PLY0513 is reported naming "function 'Show'"
    And no module is produced

  Scenario: An uncheckable contract is refused in checked mode
    Given the C# methods "public static int F(int[] a) => a.Length;"
    And the assumption "F.a: seq<u8>" in checked mode
    When the contracts are enforced
    Then error PLY0516 is reported naming "--assume-unchecked"
    And no module is produced

  Scenario Outline: Malformed assumptions are rejected
    When the assumption "<text>" is read
    Then error PLY0514 is reported naming "<text>"

    Examples:
      | text                    |
      | transform.data seq<u8>  |
      | transform.data: seq<u9> |
      | sum.n: int[10, 5]       |
      | .data: u8               |
