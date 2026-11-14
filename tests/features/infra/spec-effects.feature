Feature: Schema and validator for spec/effects/

  Scenario: The valid document passes
    Given the spec tree "tests/fixtures/spec/effects/valid"
    When I run "polypiler-tooling spec validate --root tests/fixtures/spec/effects/valid --family effects"
    Then the exit code is 0
    And the last output line is "3 documents, 0 errors"

  Scenario: Memory-mapped I/O can never be declared removable
    Given the spec document "mmio.yaml" with class "mmio", ordering "strict", duplication "forbidden" and elimination "if-unobserved"
    When I run "polypiler-tooling spec validate --family effects"
    Then stderr contains "error PLY0046: class 'mmio' requires elimination 'forbidden', found 'if-unobserved' (rule: volatile-is-strict)"
    And the exit code is 1

  Scenario Outline: A document that breaks a rule is refused with the rule's name
    Given the spec fixture "tests/fixtures/spec/effects/invalid/<fixture>"
    When I run "polypiler-tooling spec validate" on a tree containing only that fixture and the valid documents it refers to
    Then stderr contains "error <code>: <message>"
    And the exit code is 1

    Examples:
      | fixture                      | code    | message                                                                                          |
      | single-default               | PLY0046 | EFFECTS.mmio is a second default effect; EFFECTS.unknown-call already is (rule: single-default) |
      | default-is-conservative.yaml | PLY0046 | the default effect must have ordering 'strict', found 'free' (rule: default-is-conservative)    |
      | write-implies-order.yaml     | PLY0046 | ordering 'free' is not allowed for an effect that writes heap (rule: write-implies-order)       |
      | no-default                   | PLY0046 | no effect has defaultForUnknown: true (rule: single-default)                                    |
      | bad-class.yaml               | PLY0040 | /spec/class:                                                                                     |
