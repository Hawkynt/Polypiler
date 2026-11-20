@Layer.core
Feature: Proved minimum and maximum recognition
  Source fixtures are imported into the current tree IR before recognition.
  The comparison's signedness decides between the signed and unsigned operation;
  the subtract-sign form is rewritten only where the subtraction cannot overflow.

  Scenario Outline: Branchless selection becomes Min or Max with the comparison's signedness
    Given the arithmetic recognition fixture "o0120/minmax.cs" function "<form>"
    When only arithmetic rule O0120 runs with proof budget 5 seconds
    Then the arithmetic result contains intrinsic "<intrinsic>" and an SMT proof
    And both arithmetic versions map 00000005 and FFFFFFFD to <value>
    And both arithmetic versions map 80000000 and 7FFFFFFF to <extreme>
    And both arithmetic versions map 00000007 and 00000007 to 00000007

    Examples:
      | form            | intrinsic | value    | extreme  |
      | MinMask         | Min       | FFFFFFFD | 80000000 |
      | MaxMaskUnsigned | Max       | FFFFFFFD | 80000000 |
      | MinSelect       | Min       | FFFFFFFD | 80000000 |

  Scenario Outline: The subtract-sign form is rewritten when the subtraction cannot overflow
    Given the arithmetic recognition fixture "o0120/minmax.cs" function "<form>"
    When only arithmetic rule O0120 runs with proof budget 5 seconds
    Then the arithmetic result contains intrinsic "<intrinsic>" and an SMT proof
    And both arithmetic versions map 0000FFFF and 00000000 to <value>

    Examples:
      | form                   | intrinsic | value    |
      | MinSubtractSignBounded | Min       | 00000000 |
      | MaxSubtractSignBounded | Max       | 0000FFFF |

  Scenario: The subtract-sign form with unbounded operands is kept
    Given the arithmetic recognition fixture "o0120/minmax.cs" function "MinSubtractSign"
    When only arithmetic rule O0120 runs with proof budget 5 seconds
    Then the arithmetic IR is unchanged
    And the arithmetic report contains "O0120 not applied: subtraction may overflow"
    And both arithmetic versions map 7FFFFFFF and FFFFFFFF to 7FFFFFFF

  Scenario: A mask from an unrelated comparison is not a minimum
    Given the arithmetic recognition fixture "o0120/minmax.cs" function "OtherMask"
    When only arithmetic rule O0120 runs with proof budget 5 seconds
    Then the arithmetic IR is unchanged
    And the arithmetic report contains "O0120 not applied: mask is not derived from the compared operands"

  Scenario: A logical sign shift is not a sign mask
    Given the arithmetic recognition fixture "o0120/minmax.cs" function "LogicalShift"
    When only arithmetic rule O0120 runs with proof budget 5 seconds
    Then the arithmetic IR is unchanged
    And the arithmetic report contains "O0120 not applied: not equivalent to Min or Max"

  Scenario: The switch disables the rule and its reports
    Given the arithmetic recognition fixture "o0120/minmax.cs" function "MinMask"
    When arithmetic rule O0120 is disabled
    Then the arithmetic IR is unchanged
    And the arithmetic report is empty

  Scenario: A proof timeout leaves the idiom untouched
    Given the arithmetic recognition fixture "o0120/minmax.cs" function "MinMask"
    When only arithmetic rule O0120 runs with proof budget 0 seconds
    Then the arithmetic IR is unchanged
    And the arithmetic report contains "O0120 not applied: proof timeout"
