@Layer.core
Feature: Typed candidate networks
  A network is a straight-line program of bit operations from one input to one result. It evaluates in
  exact integers, lowers to the tree IR - which computes narrow values at 32 bits - and is checked by
  rules N1 to N6.

  Scenario: A hand-built nibble swap evaluates, lowers and agrees with its mapping
    Given the network "v1 = And(v0, 0x0F); v2 = ShiftLeft(v1, 4); v3 = And(v0, 0xF0); v4 = ShiftRightLogical(v3, 4); v5 = Or(v2, v4)" at width 8
    When it is evaluated on 0xB4 and its lowered IR is interpreted on 0xB4
    Then both give 0x4B
    And the lowered steps are "And, ShiftLeft, And, ShiftRight, Or" in that order
    And its cost under "bitperm-modern" is 5

  Scenario: Assembling terms adds the inversion and constant fix-ups
    Given the mapping "8:[~0,1,2,3,=1,?,?,?]" and one term emitting "v1 = And(v0, 0x0F)" with Routed 0x0F
    When the composite network is assembled
    Then its steps are "v1 = And(v0, 0x0F); v2 = Xor(v1, 0x01); v3 = Or(v2, 0x10)"
    And evaluating it on 0x05 gives 0x14

  Scenario Outline: An ill-formed network is refused
    Given the network "<network>" at width 8
    When the verifier runs
    Then the diagnostic is "PLY0810: network step <n> (<op>): <problem>"

    Examples:
      | network                          | n | op        | problem                              |
      | v1 = And(v0, v2); v2 = Not(v0)   | 1 | And       | operand v2 is not defined before use |
      | v1 = ShiftLeft(v0, 8)            | 1 | ShiftLeft | amount 8 is outside 1..7             |
      | v1 = Not(v0); v2 = And(v0, 0x0F) | 1 | Not       | is unused                            |
      | v1 = Const(0x1FF)                | 1 | Const     | constant 0x1FF does not fit 8 bits   |

  Scenario: A term that leaks bits outside its claim is refused
    Given the mapping "8:[?,?,?,?,0,1,2,3]" and one term emitting "v1 = ShiftLeft(v0, 4)" with Routed 0x30
    When the composite network is assembled
    Then the diagnostic contains "PLY0810" and "term 1 sets bits 0xC0 outside its routed mask 0x30"
