Feature: Schema and validator for spec/ir/

  Scenario: The valid document passes
    Given the spec tree "tests/fixtures/spec/ir/valid"
    When I run "polypiler-tooling spec validate --root tests/fixtures/spec/ir/valid --family ir"
    Then the exit code is 0
    And the last output line is "3 documents, 0 errors"

  Scenario: An operation without a result and without flags validates
    Given the spec document "operations/unreachable.yaml" with kind "operation", mnemonic "Unreachable", category "control", no operands, result null, flags [], traps [{when: "always", kind: unreachable}] and one example {operands: [], trap: unreachable}
    When I run "polypiler-tooling spec validate --family ir" on a tree with that document and the two stage documents
    Then the exit code is 0

  Scenario Outline: A document that breaks a rule is refused with the rule's name
    Given the spec fixture "tests/fixtures/spec/ir/invalid/<fixture>"
    When I run "polypiler-tooling spec validate" on a tree containing only that fixture and the valid documents it refers to
    Then stderr contains "error <code>: <message>"
    And the exit code is 1

    Examples:
      | fixture            | code    | message                                                                                                                                |
      | stage-range.yaml   | PLY0046 | stages.from 'IR.stage.legalized-target' (order 90) is later than stages.to 'IR.stage.source-semantic' (order 10) (rule: stage-range) |
      | example-arity.yaml | PLY0046 | example 1 has 3 operands, the operation declares 2 (rule: example-arity)                                                               |
      | flag-declared.yaml | PLY0046 | example 1 uses flag 'wrap', declared flags are mask, strict (rule: flag-declared)                                                      |
      | unknown-stage.yaml | PLY0043 | unknown spec id 'IR.stage.nowhere'                                                                                                     |
      | no-examples.yaml   | PLY0040 | /spec/examples:                                                                                                                        |
