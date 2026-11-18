@Layer.core
Feature: Proved bit reversal recognition
  The tree IR of ADR 0009 replaces the retired SSA sketches of issue 557.

  Scenario Outline: A complete reversal is proved
    Given the permutation fixture "o0105/reversal.cs" function "<form>"
    When only permutation rule O0105 runs with proof budget 5 seconds
    Then the permutation result contains intrinsic "BitReverse" and an SMT proof
    And both permutation versions map 12345678 to 1E6A2C48
    And both permutation versions map 00000001 to 80000000
    And both permutation versions map FFFFFFFF to FFFFFFFF

    Examples:
      | form       |
      | Cascade    |
      | RotateLast |
      | Loop       |

  Scenario: A partial reversal is refused
    Given the permutation fixture "o0105/reversal.cs" function "MissingHalves"
    When only permutation rule O0105 runs with proof budget 5 seconds
    Then the permutation IR is unchanged
    And the permutation report contains "O0105 not applied: not equivalent to ReverseBits"
    And both permutation versions map 00000001 to 00008000

  Scenario: A timed out proof leaves the candidate unchanged
    Given the permutation fixture "o0105/reversal.cs" function "Cascade"
    When only permutation rule O0105 runs with proof budget 0 seconds
    Then the permutation IR is unchanged
    And the permutation report contains "O0105 not applied: proof timeout"

  Scenario: Disabling the rule does not request proofs
    Given the permutation fixture "o0105/reversal.cs" function "Cascade"
    When permutation rule O0105 is disabled
    Then the permutation IR is unchanged
    And the permutation report is empty
