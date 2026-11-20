@Layer.core
Feature: Proved single-bit recognition
  Source fixtures are imported into the current tree IR before recognition.
  A shift takes its count modulo the width; the recognized operation keeps that.

  Scenario Outline: Single-bit idioms with a variable position become bit intrinsics
    Given the arithmetic recognition fixture "o0108/bits.cs" function "<form>"
    When only arithmetic rule O0108 runs with proof budget 5 seconds
    Then the arithmetic result contains intrinsic "<intrinsic>" and an SMT proof
    And both arithmetic versions give <result> for <x> at bit <n>

    Examples:
      | form           | intrinsic | x        | n  | result   |
      | TestShift      | TestBit   | 80000001 | 31 | true     |
      | TestShift      | TestBit   | 80000001 | 1  | false    |
      | TestMask       | TestBit   | 80000001 | 63 | true     |
      | TestArithmetic | TestBit   | 80000000 | 31 | true     |
      | Set            | SetBit    | 00000000 | 31 | 80000000 |
      | Set            | SetBit    | 00000000 | 33 | 00000002 |
      | Clear          | ClearBit  | FFFFFFFF | 0  | FFFFFFFE |
      | Flip           | ToggleBit | 0000000F | 4  | 0000001F |
      | SetByte        | SetBit    | 00000000 | 7  | 00000080 |
      | SetByte        | SetBit    | 00000001 | 8  | 00000001 |

  Scenario: A mask shifted at 32 bits and sign-extended to 64 bits is not a SetBit
    Given the arithmetic recognition fixture "o0108/bits.cs" function "SetNarrowOne"
    When only arithmetic rule O0108 runs with proof budget 5 seconds
    Then the arithmetic IR is unchanged
    And the arithmetic report contains "O0108 not applied: mask computed at another width"
    And both arithmetic versions give FFFFFFFF80000000 for 0 at bit 31

  Scenario: A shifted constant other than 1 is not a single-bit operation
    Given the arithmetic recognition fixture "o0108/bits.cs" function "SetTwo"
    When only arithmetic rule O0108 runs with proof budget 5 seconds
    Then the arithmetic IR is unchanged
    And the arithmetic report contains "O0108 not applied: shifted constant is not 1"

  Scenario: A two-bit field is not a bit test
    Given the arithmetic recognition fixture "o0108/bits.cs" function "Field"
    When only arithmetic rule O0108 runs with proof budget 5 seconds
    Then the arithmetic IR is unchanged
    And the arithmetic report contains "O0108 not applied: field wider than one bit"

  Scenario: The switch disables the rule and its reports
    Given the arithmetic recognition fixture "o0108/bits.cs" function "TestShift"
    When arithmetic rule O0108 is disabled
    Then the arithmetic IR is unchanged
    And the arithmetic report is empty

  Scenario: A proof timeout leaves the idiom untouched
    Given the arithmetic recognition fixture "o0108/bits.cs" function "TestShift"
    When only arithmetic rule O0108 runs with proof budget 0 seconds
    Then the arithmetic IR is unchanged
    And the arithmetic report contains "O0108 not applied: proof timeout"
