Feature: Spec metadata rules and shared validator

  Scenario: A complete normative document validates
    Given the spec document "demo/widget.yaml" with id "DEMO.widget", version 2, status "normative", reviewedRevision 2, introducedIn "P0", compatibility "additive" and tests ["tests/features/infra/spec-meta.feature"]
    When I run "polypiler-tooling spec validate --root tests/fixtures/spec/meta/valid"
    Then the exit code is 0
    And the last output line is "1 documents, 0 errors"

  Scenario Outline: A metadata rule violation names the rule and the position
    Given the spec fixture tree "<tree>"
    When I run "polypiler-tooling spec validate --root tests/fixtures/spec/meta/<tree>"
    Then stderr contains "error <code>: <message>"
    And the exit code is 1

    Examples:
      | tree             | code    | message                                                                                    |
      | unreviewed       | PLY0041 | normative document must be reviewed at its current version (reviewedRevision 1, version 2) |
      | wrong-prefix     | PLY0041 | id 'TYPES.u32' does not start with family prefix 'DEMO'                                    |
      | no-tests         | PLY0041 | normative document needs at least one test                                                 |
      | duplicate-id     | PLY0042 | duplicate spec id 'DEMO.widget', first defined in demo/a.yaml                              |
      | missing-test     | PLY0043 | test path 'tests/features/spec/nowhere.feature' does not exist                             |
      | deprecated-early | PLY0044 | deprecatedIn P1 is earlier than introducedIn P2                                            |
      | over-reviewed    | PLY0044 | reviewedRevision 3 exceeds version 2                                                       |
      | unknown-family   | PLY0047 | unknown spec family 'gadgets'                                                              |

  Scenario: A schema failure reports the JSON pointer and the source line
    Given the spec document "demo/widget.yaml" whose "version" on line 3 is the string "two"
    When I run "polypiler-tooling spec validate --root tests/fixtures/spec/meta/bad-type"
    Then stderr contains "demo/widget.yaml(3,10): error PLY0040: /version:"
    And the exit code is 1

  Scenario: One defect produces one diagnostic
    Given the spec document "demo/widget.yaml" that is not valid YAML on line 5
    When I run "polypiler-tooling spec validate --root tests/fixtures/spec/meta/broken-yaml"
    Then stderr contains exactly 1 line with "error PLY"
    And that line contains "PLY0047"

  Scenario: Validating one family leaves the others untouched
    Given a spec tree with a valid family "demo" and an invalid family "other"
    When I run "polypiler-tooling spec validate --family demo"
    Then the exit code is 0
