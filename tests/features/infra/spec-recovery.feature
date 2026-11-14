Feature: Schema and validator for spec/recovery/

  Scenario: The valid document passes
    Given the spec tree "tests/fixtures/spec/recovery/valid"
    When I run "polypiler-tooling spec validate --root tests/fixtures/spec/recovery/valid --family recovery"
    Then the exit code is 0
    And the last output line is "1 documents, 0 errors"

  Scenario: A speculative reconstruction without a fallback is refused
    Given the valid document "O0170.yaml" with proofMode changed to "speculative-guarded" and no "speculation" property
    When I run "polypiler-tooling spec validate --family recovery"
    Then stderr contains "error PLY0046: proofMode 'speculative-guarded' requires 'speculation' with guard and fallback (rule: speculation-matches-mode)"
    And the exit code is 1

  Scenario Outline: A document that breaks a rule is refused with the rule's name
    Given the spec fixture "tests/fixtures/spec/recovery/invalid/<fixture>"
    When I run "polypiler-tooling spec validate" on a tree containing only that fixture and the valid documents it refers to
    Then stderr contains "error <code>: <message>"
    And the exit code is 1

    Examples:
      | fixture                         | code    | message                                                                                                         |
      | id-in-block.yaml                | PLY0046 | O0100 is outside the recovery block O0170-O0199 (rule: id-in-block)                                             |
      | proven-needs-obligation.yaml    | PLY0046 | proofMode 'proven' requires at least one proof obligation (rule: proven-needs-obligation)                       |
      | deunroll-needs-repetitions.yaml | PLY0046 | technique 'de-unroll' requires 'minRepetitions' (rule: deunroll-needs-repetitions)                              |
      | effects-preserved.yaml          | PLY0046 | the rule rewrites stores but does not preserve 'effect-order' (rule: effects-preserved)                         |
      | no-negative-case.yaml           | PLY0040 | /spec/negativeCases:                                                                                            |
      | speculation-matches-mode.yaml   | PLY0046 | proofMode 'speculative-guarded' requires 'speculation' with guard and fallback (rule: speculation-matches-mode) |
      | switch-matches.yaml             | PLY0046 | switch '--disable-opt O0171' does not disable O0170 (rule: switch-matches)                                      |
      | layout.yaml                     | PLY0046 | id 'RECOVERY.O0171' does not match file name 'O0170.yaml' (rule: layout)                                        |
      | extra-property.yaml             | PLY0040 | /spec/author: property 'author' is not allowed                                                                  |
