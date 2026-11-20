@Layer.core
Feature: Proved zero-count loop recognition
  Source fixtures are imported into the current tree IR before recognition.
  A loop that may not end is never turned into one that does.

  Scenario Outline: A bit-scanning loop becomes a zero count
    Given the arithmetic recognition fixture "o0103/zerocount.cs" function "<form>"
    When only arithmetic rule O0103 runs with proof budget 5 seconds
    Then the arithmetic result contains intrinsic "<intrinsic>" and an SMT proof
    And both arithmetic versions count <a> as <ca>
    And both arithmetic versions count <b> as <cb>
    And both arithmetic versions count <c> as <cc>
    And both arithmetic versions count <d> as <cd>

    Examples:
      | form                     | intrinsic         | a        | ca | b        | cb | c        | cc | d        | cd |
      | TrailingGuarded          | TrailingZeroCount | 00000001 | 0  | 00000008 | 3  | 80000000 | 31 | 00000000 | 32 |
      | TrailingUnguardedNonZero | TrailingZeroCount | 00000001 | 0  | 00000008 | 3  | 80000000 | 31 | 00000000 | 32 |
      | BitLength                | LeadingZeroCount  | 00000000 | 0  | 00000001 | 1  | 12345678 | 29 | 80000000 | 32 |
      | LeadingGuarded           | LeadingZeroCount  | 00000001 | 31 | 80000000 | 0  | 00010000 | 15 | 00000000 | 32 |

  Scenario: The unguarded loop is rewritten after the branch that excludes zero
    Given the arithmetic recognition fixture "o0103/zerocount.cs" function "TrailingUnguardedNonZero"
    When only arithmetic rule O0103 runs with proof budget 5 seconds
    Then the arithmetic report contains "O0103 applied: TrailingZeroCount.u32 from unguarded loop [proof: smt-bitvector]"

  Scenario: An unguarded loop whose input may be zero is kept
    Given the arithmetic recognition fixture "o0103/zerocount.cs" function "TrailingUnguarded"
    When only arithmetic rule O0103 runs with proof budget 5 seconds
    Then the arithmetic IR is unchanged
    And the arithmetic report contains "O0103 not applied: input may be zero"
    And both arithmetic versions count 00000008 as 3

  Scenario: A bit-length loop with an arithmetic shift is kept
    Given the arithmetic recognition fixture "o0103/zerocount.cs" function "BitLengthArithmetic"
    When only arithmetic rule O0103 runs with proof budget 5 seconds
    Then the arithmetic IR is unchanged
    And the arithmetic report contains "O0103 not applied: arithmetic right shift"

  Scenario: A bound other than the width is kept
    Given the arithmetic recognition fixture "o0103/zerocount.cs" function "WrongBound"
    When only arithmetic rule O0103 runs with proof budget 5 seconds
    Then the arithmetic IR is unchanged
    And the arithmetic report contains "O0103 not applied: bound differs from width"

  Scenario: The switch disables the rule and its reports
    Given the arithmetic recognition fixture "o0103/zerocount.cs" function "TrailingGuarded"
    When arithmetic rule O0103 is disabled
    Then the arithmetic IR is unchanged
    And the arithmetic report is empty

  Scenario: A proof timeout leaves the loop untouched
    Given the arithmetic recognition fixture "o0103/zerocount.cs" function "TrailingGuarded"
    When only arithmetic rule O0103 runs with proof budget 0 seconds
    Then the arithmetic IR is unchanged
    And the arithmetic report contains "O0103 not applied: proof timeout"
