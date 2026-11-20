@Layer.core
Feature: Proved SWAR population count recognition
  Source fixtures are imported into the current tree IR before recognition.
  A structural gate selects the candidate; the proof against PopCount decides.

  Scenario Outline: The SWAR sequence becomes PopCount
    Given the arithmetic recognition fixture "o0102/popcount.cs" function "<form>"
    When only arithmetic rule O0102 runs with proof budget 5 seconds
    Then the arithmetic result contains intrinsic "PopCount" and an SMT proof
    And the arithmetic report contains "O0102 applied: PopCount.<type> from SWAR sequence [proof: smt-bitvector]"
    And both popcount versions count <x> as <count>

    Examples:
      | form       | type | x                | count |
      | Multiply32 | u32  | 12345678         | 13    |
      | Multiply32 | u32  | 80000001         | 2     |
      | Fold32     | u32  | FFFFFFFF         | 32    |
      | Multiply64 | u64  | FFFFFFFFFFFFFFFF | 64    |
      | Multiply64 | u64  | 0000000100000000 | 1     |
      | Narrow16   | u16  | FFFF             | 16    |

  Scenario: The recognized sequence and the intrinsic agree on boundary values
    Given the arithmetic recognition fixture "o0102/popcount.cs" function "Multiply32"
    When only arithmetic rule O0102 runs with proof budget 5 seconds
    Then both popcount versions count 00000000 as 0
    And both popcount versions count 80000001 as 2
    And both popcount versions count 12345678 as 13
    And both popcount versions count FFFFFFFF as 32

  Scenario Outline: A sequence that is not a population count is kept
    Given the arithmetic recognition fixture "o0102/popcount.cs" function "<form>"
    When only arithmetic rule O0102 runs with proof budget 5 seconds
    Then the arithmetic IR is unchanged
    And the arithmetic report contains "O0102 not applied: <reason>"

    Examples:
      | form            | reason                     |
      | BadMask32       | not equivalent to PopCount |
      | U64WithU32Masks | not equivalent to PopCount |
      | Shift16         | not equivalent to PopCount |
      | TwoVariables    | more than one variable     |

  Scenario: A wrong mask keeps the original result
    Given the arithmetic recognition fixture "o0102/popcount.cs" function "BadMask32"
    When only arithmetic rule O0102 runs with proof budget 5 seconds
    Then both popcount versions count 00000004 as 0

  Scenario: The switch disables the rule and its reports
    Given the arithmetic recognition fixture "o0102/popcount.cs" function "Multiply32"
    When arithmetic rule O0102 is disabled
    Then the arithmetic IR is unchanged
    And the arithmetic report is empty

  Scenario: A proof timeout leaves the sequence untouched
    Given the arithmetic recognition fixture "o0102/popcount.cs" function "Multiply32"
    When only arithmetic rule O0102 runs with proof budget 0 seconds
    Then the arithmetic IR is unchanged
    And the arithmetic report contains "O0102 not applied: proof timeout"
