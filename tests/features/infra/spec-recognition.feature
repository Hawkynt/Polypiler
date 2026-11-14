Feature: Schema and validator for spec/recognition/

  Scenario: The valid document passes
    Given the spec tree "tests/fixtures/spec/recognition/valid"
    When I run "polypiler-tooling spec validate --root tests/fixtures/spec/recognition/valid --family recognition"
    Then the exit code is 0
    And the last output line is "1 documents, 0 errors"

  Scenario: A rule without a counterexample is refused
    Given the valid document "O0100.yaml" with its "counterexamples" list emptied
    When I run "polypiler-tooling spec validate --family recognition"
    Then stderr contains "error PLY0040: /spec/counterexamples:"
    And the exit code is 1

  Scenario Outline: A document that breaks a rule is refused with the rule's name
    Given the spec fixture "tests/fixtures/spec/recognition/invalid/<fixture>"
    When I run "polypiler-tooling spec validate" on a tree containing only that fixture and the valid documents it refers to
    Then stderr contains "error <code>: <message>"
    And the exit code is 1

    Examples:
      | fixture                     | code    | message                                                                                                |
      | id-in-block.yaml            | PLY0046 | O0042 is outside the recognition blocks O0100-O0169 and O0200-O0229 (rule: id-in-block)                |
      | switch-matches.yaml         | PLY0046 | switch '--disable-opt O0101' does not disable O0100 (rule: switch-matches)                             |
      | counterexample-differs.yaml | PLY0046 | counterexamples[0] has equal naive and correct results (0x00000080) (rule: counterexample-differs)     |
      | smt-needs-budget.yaml       | PLY0046 | obligation 'rotl-wide' of class smt-bitvector has no budgetSeconds (rule: smt-needs-budget)            |
      | exhaustive-width.yaml       | PLY0046 | obligation 'rotl-8' of class exhaustive-8bit lists width 32 (rule: exhaustive-width)                   |
      | layout.yaml                 | PLY0046 | id 'RECOGNITION.O0101' does not match file name 'O0100.yaml' (rule: layout)                            |
      | no-proof.yaml               | PLY0040 | /spec/proofObligations:                                                                                |
      | extra-property.yaml         | PLY0040 | /spec/author: property 'author' is not allowed                                                         |
