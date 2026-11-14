@Layer.core
Feature: Typed semantic hierarchy of bit transforms

  Scenario: A reversal narrows to a bit permutation
    Given the mapping "8:[7,6,5,4,3,2,1,0]"
    When it is classified
    Then the class is "Permutation"
    And BitPermutation.From succeeds

  Scenario: A mapping that duplicates a bit is a routing and cannot be declared a permutation
    Given BitPerm's duplication mapping "8:[0,?,0,?,0,?,?,?]"
    When it is classified
    Then the class is "Routing"
    And BitPermutation.TryFrom reports "PLY0805: mapping is declared BitPermutation but input bit 0 is used 3 times"

  Scenario: An inverted bit makes a square, injective mapping a routing
    Given the mapping "4:[~0,1,2,3]"
    When it is classified
    Then the class is "Routing"
    And the explanation is "output bit 0 is inverted"

  Scenario: An XOR of two inputs is a Boolean transform, not a routing
    Given a Boolean transform of 2 inputs whose output 0 is "Xor(Input 0, Input 1)" and output 1 is "Input 1"
    When TryAsRouting is called
    Then it returns false with reason "output bit 0 depends on 2 input bits"

  Scenario: A Boolean expression that folds to a single input is a routing
    Given a Boolean transform of 4 inputs whose outputs are "Xor(Input 3, Const 1)", "And(Input 0, Input 0)", "Const 0", "Input 2"
    When TryAsRouting is called
    Then it returns the routing "4:[~3,0,=0,2]"

  Scenario: A condition must agree with the mapping's don't-cares
    Given the mapping "4:[0,?,2,3]" and a condition whose RequiredOutputMask is 0xF
    When the routing is verified
    Then the diagnostic is "PLY0805: output bit 1 is don't-care in the mapping but required by the condition"
