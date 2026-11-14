Feature: Schema and validator for spec/verification/

  Scenario: The valid document passes
    Given the spec tree "tests/fixtures/spec/verification/valid"
    When I run "polypiler-tooling spec validate --root tests/fixtures/spec/verification/valid"
    Then the exit code is 0
    And the last output line is "8 documents, 0 errors"

  Scenario: A random generator without a seed is refused
    Given the spec document "generators/u32-uniform.yaml" with type "TYPES.u32", strategy "uniform", count 10000 and no seed
    When I run "polypiler-tooling spec validate --family verification"
    Then stderr contains "error PLY0046: strategy 'uniform' requires 'seed' (rule: strategy-requirements)"
    And the exit code is 1

  Scenario Outline: A document that breaks a rule is refused with the rule's name
    Given the spec fixture "tests/fixtures/spec/verification/invalid/<fixture>"
    When I run "polypiler-tooling spec validate --family verification" on a tree containing only that fixture and the valid documents it refers to
    Then stderr contains "error <code>: <message>"
    And the exit code is 1

    Examples:
      | fixture                          | code    | message                                                                                                       |
      | boundary-covers-extremes.yaml    | PLY0046 | boundary generator for TYPES.u32 lacks the maximum 0xFFFFFFFF (rule: boundary-covers-extremes)                |
      | value-fits-type.yaml             | PLY0046 | values[5] 0x100000000 does not fit TYPES.u32 (32 bits) (rule: value-fits-type)                                |
      | bound-for-mode.yaml              | PLY0046 | mode 'ulp' requires 'bound' (rule: bound-for-mode)                                                            |
      | one-tolerance-per-operation.yaml | PLY0046 | operation INTRINSICS.Sin already has tolerance VERIFICATION.tolerance.sin (rule: one-tolerance-per-operation) |
      | oracle-not-self.yaml             | PLY0046 | an oracle must be independent of Polypiler, found implementation 'polypiler' (rule: oracle-not-self)          |
      | unknown-type.yaml                | PLY0043 | unknown spec id 'TYPES.u33'                                                                                   |
