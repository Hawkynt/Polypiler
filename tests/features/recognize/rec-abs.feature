@Layer.core
Feature: Proved wrapping absolute value recognition
  Source fixtures are imported into the current tree IR before recognition.
  The smallest signed value wraps to itself; trapping absolute value is a different operation.

  Scenario Outline: A sign-mask or select idiom is proved
    Given the arithmetic recognition fixture "o0121/absolute.cs" function "<form>"
    When only arithmetic rule O0121 runs with proof budget 5 seconds
    Then the arithmetic result contains intrinsic "AbsWrap" and an SMT proof
    And both arithmetic versions map FFFFFFFB to 00000005
    And both arithmetic versions map 00000007 to 00000007
    And both arithmetic versions map 00000000 to 00000000
    And both arithmetic versions map 80000000 to 80000000

    Examples:
      | form          |
      | XorSubtract   |
      | AddXor        |
      | CompareSelect |

  Scenario: A logical shift does not produce a sign mask
    Given the arithmetic recognition fixture "o0121/absolute.cs" function "Logical"
    When only arithmetic rule O0121 runs with proof budget 5 seconds
    Then the arithmetic IR is unchanged
    And the arithmetic report contains "O0121 not applied: logical right shift"
    And both arithmetic versions map FFFFFFFE to FFFFFFFE

  Scenario: A shift below width minus one is not a sign mask
    Given the arithmetic recognition fixture "o0121/absolute.cs" function "WrongShift"
    When only arithmetic rule O0121 runs with proof budget 5 seconds
    Then the arithmetic IR is unchanged
    And the arithmetic report contains "O0121 not applied: shift amount is not width-1"

  Scenario: The switch disables the rule and its reports
    Given the arithmetic recognition fixture "o0121/absolute.cs" function "XorSubtract"
    When arithmetic rule O0121 is disabled
    Then the arithmetic IR is unchanged
    And the arithmetic report is empty

  Scenario: A proof timeout leaves the idiom untouched
    Given the arithmetic recognition fixture "o0121/absolute.cs" function "XorSubtract"
    When only arithmetic rule O0121 runs with proof budget 0 seconds
    Then the arithmetic IR is unchanged
    And the arithmetic report contains "O0121 not applied: proof timeout"
