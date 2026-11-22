@Layer.core
Feature: Proved de Bruijn zero-count recognition
  Source fixtures are imported into the current tree IR before recognition.
  The lookup reads the table's first entry for zero; the replacement keeps that value.

  Scenario Outline: A de Bruijn bit scan becomes a zero count with the zero case preserved
    Given the arithmetic recognition fixture "o0104/debruijn.cs" function "<form>"
    When only arithmetic rule O0104 runs with proof budget 5 seconds
    Then the arithmetic result contains intrinsic "<intrinsic>" and an SMT proof
    And both arithmetic versions count <first> as <first count>
    And both arithmetic versions count <second> as <second count>
    And both arithmetic versions count 0 as 0

    Examples:
      | form            | intrinsic         | first    | first count | second           | second count |
      | TrailingZeros   | TrailingZeroCount | 10000000 | 28          | 80000000         | 31           |
      | FloorLog2       | LeadingZeroCount  | 12345678 | 28          | FFFFFFFF         | 31           |
      | TrailingZeros64 | TrailingZeroCount | 10000000000 | 40       | 8000000000000000 | 63           |
      | FloorLog64      | LeadingZeroCount  | 12345678 | 28          | FFFFFFFFFFFFFFFF | 63           |

  Scenario: A table with a wrong entry is not rewritten
    Given the arithmetic recognition fixture "o0104/debruijn.cs" function "WrongEntry"
    When only arithmetic rule O0104 runs with proof budget 5 seconds
    Then the arithmetic IR is unchanged
    And the arithmetic report contains "O0104 not applied: table check failed at index 2"
    And both arithmetic versions count 10000000 as 27

  Scenario: A table that may be written is not rewritten
    Given the arithmetic recognition fixture "o0104/debruijn.cs" function "WrittenTable"
    When only arithmetic rule O0104 runs with proof budget 5 seconds
    Then the arithmetic IR is unchanged
    And the arithmetic report contains "O0104 not applied: table not constant"

  Scenario: Clearing the lowest bit is not isolating it
    Given the arithmetic recognition fixture "o0104/debruijn.cs" function "ClearsLowest"
    When only arithmetic rule O0104 runs with proof budget 5 seconds
    Then the arithmetic IR is unchanged
    And the arithmetic report contains "O0104 not applied: operand is not an isolated bit"

  Scenario: The switch disables the rule and its reports
    Given the arithmetic recognition fixture "o0104/debruijn.cs" function "TrailingZeros"
    When arithmetic rule O0104 is disabled
    Then the arithmetic IR is unchanged
    And the arithmetic report is empty

  Scenario: A proof timeout leaves the lookup untouched
    Given the arithmetic recognition fixture "o0104/debruijn.cs" function "TrailingZeros"
    When only arithmetic rule O0104 runs with proof budget 0 seconds
    Then the arithmetic IR is unchanged
    And the arithmetic report contains "O0104 not applied: proof timeout"
