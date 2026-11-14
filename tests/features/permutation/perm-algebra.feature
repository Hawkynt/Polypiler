Feature: Canonical algebra of bit mappings

  Scenario: Composition applies the inner permutation first
    Given P1 = "8:[5,6,7,0,1,2,3,4]" (rotate left by 3) and P2 = "8:[7,6,5,4,3,2,1,0]" (reverse)
    When I compose outer P2 with inner P1
    Then the result is "8:[4,3,2,1,0,7,6,5]"
    And evaluating it on 0xB4 gives 0xA5, the same as reversing the rotation of 0xB4

  Scenario: The inverse of a rotation cancels it
    Given P = "8:[5,6,7,0,1,2,3,4]"
    When I invert it
    Then the inverse is "8:[3,4,5,6,7,0,1,2]"
    And composing P with its inverse in either order gives the identity
    And the order of P is 8

  Scenario: A routing with duplication has no inverse
    Given the mapping "8:[0,0,2,3,4,5,6,7]"
    When TryInverse is called
    Then it returns false with "PLY0803: mapping is not a bijection (input bit 0 is used 2 times); it has no inverse"

  Scenario: XOR does not distribute over a routing that sets a constant
    Given the mapping "2:[0,=1]"
    When Distribution.OverXor is asked
    Then it is false
    And the counterexample a = 0, b = 0 gives 2 for the routed XOR and 0 for the XOR of the routed values

  Scenario Outline: The normal form names a mapping
    Given the mapping "<mapping>"
    When it is described
    Then the shape is "<shape>"

    Examples:
      | mapping                                     | shape              |
      | 8:[0,1,2,3,4,5,6,7]                         | Identity           |
      | 8:[4,5,6,7,0,1,2,3]                         | Rotation(4)        |
      | 8:[7,6,5,4,3,2,1,0]                         | ReverseBits        |
      | 16:[8,9,10,11,12,13,14,15,0,1,2,3,4,5,6,7]  | Rotation(8)        |
      | 16:[7,6,5,4,3,2,1,0,15,14,13,12,11,10,9,8]  | ReverseBitsInBytes |
      | 8:[1,3,5,7,=0,=0,=0,=0]                     | Compress(0xAA)     |
      | 8:[0,=0,1,=0,2,=0,3,=0]                     | Expand(0x55)       |
      | 8:[1,0,3,2,5,4,7,6]                         | Permutation        |
      | 8:[0,0,2,3,4,5,6,7]                         | Routing            |
