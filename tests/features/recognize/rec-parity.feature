@Layer.core
Feature: Proved parity recognition
  Source fixtures are imported into the current tree IR before recognition.
  The parity is 1 for an odd number of set bits; a fold that leaves out bits is not.

  Scenario Outline: A parity computation becomes Parity
    Given the arithmetic recognition fixture "o0110/parity.cs" function "<form>"
    When only arithmetic rule O0110 runs with proof budget 5 seconds
    Then the arithmetic result contains intrinsic "Parity" and an SMT proof
    And both arithmetic versions map <zero> to 00000000
    And both arithmetic versions map <one> to 00000001
    And both arithmetic versions map <even> to 00000000
    And both arithmetic versions map <odd> to 00000001

    Examples:
      | form     | zero     | one      | even     | odd      |
      | Fold32   | 00000000 | 00000001 | 80000001 | 12345678 |
      | Nibble8  | 00       | 01       | 03       | FE       |
      | Nibble32 | 00000000 | 80000000 | FFFFFFFF | FFFFFFFE |
      | Signed   | 00000000 | 80000000 | 80000001 | 7FFFFFFF |

  Scenario: A fold that skips a step is not the parity of the word
    Given the arithmetic recognition fixture "o0110/parity.cs" function "SkipsAStep"
    When only arithmetic rule O0110 runs with proof budget 5 seconds
    Then the arithmetic IR is unchanged
    And the arithmetic report contains "O0110 not applied: not equivalent to parity"
    And both arithmetic versions map 00000010 to 00000000

  Scenario: The complement table gives the even parity bit
    Given the arithmetic recognition fixture "o0110/parity.cs" function "ComplementTable"
    When only arithmetic rule O0110 runs with proof budget 5 seconds
    Then the arithmetic IR is unchanged
    And the arithmetic report contains "O0110 not applied: not equivalent to parity"
    And both arithmetic versions map 01 to 00000000

  Scenario: A final mask of 3 keeps two bits
    Given the arithmetic recognition fixture "o0110/parity.cs" function "MaskThree"
    When only arithmetic rule O0110 runs with proof budget 5 seconds
    Then the arithmetic IR is unchanged
    And the arithmetic report contains "O0110 not applied: not equivalent to parity"

  Scenario: The switch disables the rule and its reports
    Given the arithmetic recognition fixture "o0110/parity.cs" function "Fold32"
    When arithmetic rule O0110 is disabled
    Then the arithmetic IR is unchanged
    And the arithmetic report is empty

  Scenario: A proof timeout leaves the idiom untouched
    Given the arithmetic recognition fixture "o0110/parity.cs" function "Fold32"
    When only arithmetic rule O0110 runs with proof budget 0 seconds
    Then the arithmetic IR is unchanged
    And the arithmetic report contains "O0110 not applied: proof timeout"
