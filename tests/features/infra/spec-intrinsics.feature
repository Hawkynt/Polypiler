Feature: Schema and validator for spec/intrinsics/

  Scenario: The valid document passes
    Given the spec tree "tests/fixtures/spec/intrinsics/valid"
    When I run "polypiler-tooling spec validate --root tests/fixtures/spec/intrinsics/valid"
    Then the exit code is 0
    And the last output line is "9 documents, 0 errors"

  Scenario: An inexact intrinsic must name its tolerance policy
    Given the spec document "trig/sin.yaml" with group "Trig" and exactness {mode: tolerance} without "policy"
    When I run "polypiler-tooling spec validate --family intrinsics"
    Then stderr contains "error PLY0040: /spec/exactness:"
    And the exit code is 1

  Scenario Outline: A document that breaks a rule is refused with the rule's name
    Given the spec fixture "tests/fixtures/spec/intrinsics/invalid/<fixture>"
    When I run "polypiler-tooling spec validate" on a tree containing only that fixture and the valid documents it refers to
    Then stderr contains "error <code>: <message>"
    And the exit code is 1

    Examples:
      | fixture               | code    | message                                                                                |
      | vector-arity.yaml     | PLY0046 | vectors[0] has 2 inputs, the intrinsic declares 1 parameter (rule: vector-arity)       |
      | vector-width.yaml     | PLY0046 | vectors[1].inputs[0] 0x1FFFFFFFF does not fit TYPES.u32 (32 bits) (rule: vector-width) |
      | integer-is-exact.yaml | PLY0046 | group 'BitCount' must be exact, found mode 'tolerance' (rule: integer-is-exact)        |
      | two-vectors.yaml      | PLY0040 | /spec/vectors:                                                                         |
      | unknown-type.yaml     | PLY0043 | unknown spec id 'TYPES.u33'                                                            |
