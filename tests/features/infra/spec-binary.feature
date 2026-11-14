Feature: Schema and validator for spec/binary/

  Scenario: The valid document passes
    Given the spec tree "tests/fixtures/spec/binary/valid"
    When I run "polypiler-tooling spec validate --root tests/fixtures/spec/binary/valid --family binary"
    Then the exit code is 0
    And the last output line is "2 documents, 0 errors"

  Scenario: A variant document may extend a parent table
    Given the spec document "wdc65c02/instructions.yaml" with cpu {name: wdc65c02, variant: cmos, parent: BINARY.mos6502.instructions, byteOrder: little} adding opcode "0x80" BRA with mode "relative" (operandBytes 1) and length 2
    When I run "polypiler-tooling spec validate --family binary" on the valid tree plus that document
    Then the exit code is 0
    And changing its parent to "BINARY.mos6510.instructions" gives "error PLY0043: unknown spec id 'BINARY.mos6510.instructions'"

  Scenario Outline: A document that breaks a rule is refused with the rule's name
    Given the spec fixture "tests/fixtures/spec/binary/invalid/<fixture>"
    When I run "polypiler-tooling spec validate" on a tree containing only that fixture and the valid documents it refers to
    Then stderr contains "error <code>: <message>"
    And the exit code is 1

    Examples:
      | fixture                  | code    | message                                                                                            |
      | opcode-unique.yaml       | PLY0046 | opcode 0xA9 is defined twice (LDA immediate, LDX immediate) (rule: opcode-unique)                  |
      | length-matches-mode.yaml | PLY0046 | instruction 0xA9 has length 3, opcode (1) + mode 'immediate' (1) is 2 (rule: length-matches-mode) |
      | flags-declared.yaml      | PLY0046 | instruction 0x69 affects flag 'H', declared flags are N, V, B, D, I, Z, C (rule: flags-declared)   |
      | token-in-range.yaml      | PLY0046 | token 0x7F (X) is outside the range 0x80-0xCB (rule: token-in-range)                               |
      | token-unique.yaml        | PLY0046 | token value 0x99 is defined twice (PRINT, ?) (rule: token-unique)                                  |
