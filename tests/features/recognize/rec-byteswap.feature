@Layer.core
Feature: Proved byte swap recognition
  Source fixtures are imported into the current tree IR before recognition.

  Scenario Outline: A byte expression is proved
    Given the permutation fixture "o0106/byteswap.cs" function "<form>"
    When only permutation rule O0106 runs with proof budget 5 seconds
    Then the permutation result contains intrinsic "ByteSwap" and an SMT proof
    And both permutation versions map 12345678 to 78563412

    Examples:
      | form    |
      | Terms   |
      | Cascade |

  Scenario: A counted byte loop is proved
    Given the permutation fixture "o0106/byteswap.cs" function "Loop"
    When only permutation rule O0106 runs with proof budget 5 seconds
    Then the permutation result contains intrinsic "ByteSwap" and an SMT proof
    And both permutation versions map 0102030405060708 to 0807060504030201

  Scenario: Unmasked sign copies are not a byte reversal
    Given the permutation fixture "o0106/byteswap.cs" function "SignCopies"
    When only permutation rule O0106 runs with proof budget 5 seconds
    Then the permutation IR is unchanged
    And the permutation report contains "O0106 not applied: sign bits reach the result"
    And both permutation versions map 80000000 to FFFFFF80

  Scenario: A word swap is not a byte swap
    Given the permutation fixture "o0106/byteswap.cs" function "WordSwap"
    When only permutation rule O0106 runs with proof budget 5 seconds
    Then the permutation IR is unchanged
    And the permutation report contains "O0106 not applied: byte provenance is not a reversal"

  Scenario: A byte swap proof may time out
    Given the permutation fixture "o0106/byteswap.cs" function "Terms"
    When only permutation rule O0106 runs with proof budget 0 seconds
    Then the permutation IR is unchanged
    And the permutation report contains "O0106 not applied: proof timeout"
