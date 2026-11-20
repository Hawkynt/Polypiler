@Layer.core
Feature: Recovered division and remainder by a constant
  Source fixtures are imported into the current tree IR before recognition.
  The divisor is derived from the multiplier and the shift and certified in exact integers;
  nothing is looked up in a table of known magic numbers.

  Scenario Outline: A certified reciprocal sequence becomes a division
    Given the reciprocal division fixture function "<form>"
    When only rule O0126 runs
    Then the result is a division by <d> on a validated certificate
    And both divided versions map <x> to <q>

    Examples:
      | form         | d  | x                | q                |
      | Udiv10       | 10 | FFFFFFFF         | 19999999         |
      | Udiv7AddBack | 7  | FFFFFFFF         | 24924924         |
      | Udiv10Narrow | 10 | FFFF             | 1999             |
      | Sdiv10       | 10 | FFFFFFEF         | FFFFFFFF         |
      | Sdiv10       | 10 | 80000000         | F3333334         |
      | Udiv3Wide    | 3  | FFFFFFFFFFFFFFFF | 5555555555555555 |

  Scenario: The remainder idiom becomes a remainder
    Given the reciprocal division fixture function "Urem10"
    When only rule O0126 runs
    Then the result is a remainder by 10 on a validated certificate
    And both divided versions map FFFFFFFF to 00000005

  Scenario: A magic constant that is off by one is not a division
    Given the reciprocal division fixture function "Udiv10BadMagic"
    When only rule O0126 runs
    Then the divided IR is unchanged
    And the division report contains "O0126 not applied: reciprocal is not exact for the operand range"
    And both divided versions map 0000000A to 00000000

  Scenario: A 16-bit reciprocal on an unbounded 32-bit value is not a division
    Given the reciprocal division fixture function "Udiv10NarrowMul"
    When only rule O0126 runs
    Then the divided IR is unchanged
    And the division report contains "O0126 not applied: multiplication may wrap"
    And both divided versions map 00014000 to 00000000

  Scenario: A signed reciprocal without its sign correction is not a truncating division
    Given the reciprocal division fixture function "Sdiv10Uncorrected"
    When only rule O0126 runs
    Then the divided IR is unchanged
    And the division report contains "O0126 not applied: missing sign correction"
    And both divided versions map FFFFFFEF to FFFFFFFE

  Scenario: A remainder by another divisor is not a remainder
    Given the reciprocal division fixture function "Urem10By9"
    When only rule O0126 runs
    Then the division report contains "O0126 not applied: remainder uses another divisor"

  Scenario: The switch disables the rule
    Given the reciprocal division fixture function "Udiv10"
    When rule O0126 is disabled
    Then the divided IR is unchanged
    And the division report is empty
