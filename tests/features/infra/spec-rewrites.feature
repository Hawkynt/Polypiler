@Layer.core
Feature: Schema and validator for spec/rewrites/

  Scenario: The valid document passes
    Given the spec tree "tests/fixtures/spec/rewrites/valid"
    When I run "polypiler-tooling spec validate --root tests/fixtures/spec/rewrites/valid --family rewrites"
    Then the exit code is 0
    And the last output line is "1 documents, 0 errors"

  Scenario: Floating-point reassociation without an exactness proof is refused
    Given a rewrite document "O0002.yaml" turning "(a + b) - a" into "b" with floatingPoint {touchesFloat: true, reassociates: true, contracts: false} and proof class "smt-bitvector"
    When I run "polypiler-tooling spec validate --family rewrites"
    Then stderr contains "error PLY0046: floating-point reassociation requires proof class float-exactness, found smt-bitvector (rule: no-unproven-reassociation)"
    And the exit code is 1

  Scenario Outline: A document that breaks a rule is refused with the rule's name
    Given the spec fixture "tests/fixtures/spec/rewrites/invalid/<fixture>"
    When I run "polypiler-tooling spec validate" on a tree containing only that fixture and the valid documents it refers to
    Then stderr contains "error <code>: <message>"
    And the exit code is 1

    Examples:
      | fixture                      | code    | message                                                                                                 |
      | id-in-block.yaml             | PLY0046 | O0150 is outside the rewrite blocks O0001-O0099 and O0230-O0299 (rule: id-in-block)                     |
      | float-flags-consistent.yaml  | PLY0046 | floatingPoint.reassociates is true but touchesFloat is false (rule: float-flags-consistent)             |
      | interaction-acyclic          | PLY0046 | interaction cycle O0001 -> O0002 -> O0001 (rule: interaction-acyclic)                                   |
      | switch-matches.yaml          | PLY0046 | switch '--disable-opt O0002' does not disable O0001 (rule: switch-matches)                              |
      | no-negative-fixture.yaml     | PLY0040 | /spec/negativeFixtures:                                                                                 |
      | no-unproven-reassociation.yaml | PLY0046 | floating-point reassociation requires proof class float-exactness, found smt-bitvector (rule: no-unproven-reassociation) |
      | negative-fixture-exists.yaml | PLY0046 | negative fixture tests/fixtures/optimize/o0001/overflow.pir does not exist (rule: negative-fixture-exists) |
      | layout                       | PLY0046 | id 'REWRITES.O0002' does not match file name 'O0001.yaml' (rule: layout)                                |
      | interaction-unknown.yaml     | PLY0043 | unknown spec id 'REWRITES.O0050'                                                                        |
