@Layer.core
Feature: Canonical mapping evaluator

  Scenario Outline: Evaluating a mapping bit by bit
    Given the mapping "<mapping>"
    When it is evaluated on <input>
    Then the result is <output>

    Examples:
      | mapping                 | input | output |
      | 8:[7,6,5,4,3,2,1,0]     | 0xB4  | 0x2D   |
      | 8:[4,5,6,7,0,1,2,3]     | 0xB4  | 0x4B   |
      | 8:[5,6,7,0,1,2,3,4]     | 0x81  | 0x0C   |
      | 8:[0,0,~1,=1,?,7,6,5]   | 0xB4  | 0xAC   |
      | 8:[0,0,~1,=1,?,7,6,5]   | 0xFF  | 0xEB   |
      | 8:[0,0,~1,=1,?,7,6,5]   | 0x00  | 0x0C   |
      | 8:[0,2,4,6]             | 0x55  | 0xF    |

  Scenario: Every exported BitPerm vector is reproduced
    Given the 44 mapping and vector files under "tests/fixtures/bitperm"
    When each input is evaluated with MappingEvaluator
    Then every result equals the fixture output on the fixture mask
    And the number of mismatches is 0

  Scenario: A wide logical width is evaluated without a register claim
    Given BitMapping.Reverse(4096)
    When it is evaluated on the value with only bit 0 set, as 64 limbs
    Then only bit 4095 of the result is set

  Scenario: An input that does not fit the input width is refused
    Given the mapping "8:[7,6,5,4,3,2,1,0]"
    When it is evaluated on 0x100
    Then an ArgumentOutOfRangeException with message "input 0x100 does not fit 8 bits" is thrown

  Scenario: An input outside the declared domain is refused
    Given the routing "8:[0,1,2,3,=0,=0,=0,=0]" with condition InputKnownZero 0xF0
    When EvaluateChecked is called with 0x1F
    Then the diagnostic is "PLY0806: input 0x1F is outside the declared input domain"
