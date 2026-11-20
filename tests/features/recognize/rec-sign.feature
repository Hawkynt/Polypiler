@Layer.core
Feature: Proved signum recognition
  Source fixtures are imported into the current tree IR before recognition.
  The sign is -1, 0 or 1; a two-valued sign mask is a different operation.

  Scenario Outline: A three-way sign idiom becomes Sign
    Given the arithmetic recognition fixture "o0122/sign.cs" function "<form>"
    When only arithmetic rule O0122 runs with proof budget 5 seconds
    Then the arithmetic result contains intrinsic "Sign" and an SMT proof
    And both arithmetic versions map 00000005 to 00000001
    And both arithmetic versions map FFFFFFFB to FFFFFFFF
    And both arithmetic versions map 00000000 to 00000000
    And both arithmetic versions map 80000000 to FFFFFFFF
    And both arithmetic versions map 7FFFFFFF to 00000001

    Examples:
      | form              |
      | CompareDifference |
      | ShiftOr           |
      | Select            |

  Scenario: The two-valued sign is not a signum
    Given the arithmetic recognition fixture "o0122/sign.cs" function "OrOne"
    When only arithmetic rule O0122 runs with proof budget 5 seconds
    Then the arithmetic IR is unchanged
    And the arithmetic report contains "O0122 not applied: not equivalent to Sign"
    And both arithmetic versions map 00000000 to 00000001

  Scenario: An arithmetic shift of the negated operand is not a signum
    Given the arithmetic recognition fixture "o0122/sign.cs" function "ShiftOrArithmetic"
    When only arithmetic rule O0122 runs with proof budget 5 seconds
    Then the arithmetic IR is unchanged
    And the arithmetic report contains "O0122 not applied: not equivalent to Sign"
    And both arithmetic versions map 00000005 to FFFFFFFF

  Scenario: Unsigned comparisons are not a signum
    Given the arithmetic recognition fixture "o0122/sign.cs" function "Unsigned"
    When only arithmetic rule O0122 runs with proof budget 5 seconds
    Then the arithmetic IR is unchanged
    And the arithmetic report contains "O0122 not applied: unsigned comparison"

  Scenario: The switch disables the rule and its reports
    Given the arithmetic recognition fixture "o0122/sign.cs" function "CompareDifference"
    When arithmetic rule O0122 is disabled
    Then the arithmetic IR is unchanged
    And the arithmetic report is empty

  Scenario: A proof timeout leaves the idiom untouched
    Given the arithmetic recognition fixture "o0122/sign.cs" function "CompareDifference"
    When only arithmetic rule O0122 runs with proof budget 0 seconds
    Then the arithmetic IR is unchanged
    And the arithmetic report contains "O0122 not applied: proof timeout"
