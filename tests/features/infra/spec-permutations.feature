@Layer.core
Feature: Schema and validator for spec/permutations/

  Scenario: The valid document passes
    Given the spec tree "tests/fixtures/spec/permutations/valid"
    When I run "polypiler-tooling spec validate --root tests/fixtures/spec/permutations/valid --family permutations"
    Then the exit code is 0
    And the last output line is "1 documents, 0 errors"

  Scenario: A routing that duplicates a bit cannot be declared bijective
    Given the spec document "mappings/spread.yaml" with inputWidth 4, outputs [0, 1, 3, 3] and bijective true
    When I run "polypiler-tooling spec validate --family permutations"
    Then stderr contains "error PLY0046: mapping is declared bijective but input bit 3 is used twice (rule: bijective-consistent)"
    And the exit code is 1

  Scenario Outline: A document that breaks a rule is refused with the rule's name
    Given the spec fixture "tests/fixtures/spec/permutations/invalid/<fixture>"
    When I run "polypiler-tooling spec validate" on a tree containing only that fixture and the valid documents it refers to
    Then stderr contains "error <code>: <message>"
    And the exit code is 1

    Examples:
      | fixture                | code    | message                                                                                             |
      | index-in-range.yaml    | PLY0046 | outputs[3] reads input bit 8, inputWidth is 8 (rule: index-in-range)                                |
      | vector-consistent.yaml | PLY0046 | vectors[1]: the mapping gives 0x2D for input 0xB4, the document says 0x2C (rule: vector-consistent) |
      | vector-width.yaml      | PLY0046 | vectors[0].input 0x100 does not fit inputWidth 8 (rule: vector-width)                               |
      | cost-evidence.yaml     | PLY0046 | measured cost evidence docs/benchmarks/6502-shift.json does not exist (rule: cost-evidence)         |
      | one-vector.yaml        | PLY0040 | /spec/vectors:                                                                                      |
