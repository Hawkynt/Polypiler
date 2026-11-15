@Layer.core
Feature: Schema and validator for spec/types/

  Scenario: The valid document passes
    Given the spec tree "tests/fixtures/spec/types/valid"
    When I run "polypiler-tooling spec validate --root tests/fixtures/spec/types/valid --family types"
    Then the exit code is 0
    And the last output line is "3 documents, 0 errors"

  Scenario: Two formats of the same size stay distinct
    Given the spec documents "float/f32.yaml" and "mbf/mbf32.yaml", both with bits 32 and layout sizeBytes 4, each listing the other in distinctFrom
    When I run "polypiler-tooling spec validate --root tests/fixtures/spec/types/valid --family types"
    Then the exit code is 0
    When I remove "TYPES.f32" from the distinctFrom of "mbf/mbf32.yaml"
    And I run "polypiler-tooling spec validate --root tests/fixtures/spec/types/valid --family types"
    Then the exit code is 1
    And stderr contains "types/float/f32.yaml(21,18): error PLY0046: TYPES.f32 lists TYPES.mbf32 as distinct, but TYPES.mbf32 does not list TYPES.f32 (rule: distinct-symmetric)"

  Scenario Outline: A document that breaks a rule is refused with the rule's name
    Given the spec fixture "tests/fixtures/spec/types/invalid/<fixture>" with the valid documents it refers to
    When I run "polypiler-tooling spec validate --family types"
    Then stderr contains "error <code>: <message>"
    And the exit code is 1

    Examples:
      | fixture                     | code    | message                                                                         |
      | bits-fit-layout.yaml        | PLY0046 | bits 32 do not fit layout.sizeBytes 2 (rule: bits-fit-layout)                   |
      | example-width.yaml          | PLY0046 | values[0].bits 0x1FFFFFFFF needs 33 bits, the type has 32 (rule: example-width) |
      | alignment-power-of-two.yaml | PLY0046 | layout.alignment 3 is not a power of two (rule: alignment-power-of-two)         |
      | family-requirements.yaml    | PLY0046 | family 'float' requires 'rounding' (rule: family-requirements)                  |
      | unknown-encoding-target.yaml | PLY0043 | unknown spec id 'TYPES.i31'                                                    |
      | distinct-symmetric.yaml     | PLY0046 | TYPES.distinct-symmetric lists TYPES.u32 as distinct, but TYPES.u32 does not list TYPES.distinct-symmetric (rule: distinct-symmetric) |
      | layout.yaml                 | PLY0046 | id 'TYPES.misplaced' does not follow from the file name, expected 'TYPES.layout' (rule: layout) |
