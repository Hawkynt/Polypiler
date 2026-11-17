@Layer.core
Feature: Capability report from executed evidence

  @EndToEnd
  Scenario: Without gate records every pair is planned
    Given a spec tree with source profiles "csharp/net10" and "javascript/node-es2022" and target profiles "csharp/net10" and "javascript/node-es2022"
    And an empty gate directory
    When I run "polypiler-tooling report capabilities --gates artifacts/gates"
    Then "docs/capabilities.json" lists 4 pairs
    And every pair has state "planned", fixtures 0 and evidence null

  @EndToEnd
  Scenario: A verified pair is reported at its achieved state and the others stay planned
    Given the same spec tree
    And a valid gate record for "csharp/net10 -> javascript/node-es2022" whose single fixture has evidence up to "verified"
    When I run "polypiler-tooling report capabilities --gates artifacts/gates"
    Then the pair "csharp/net10 -> javascript/node-es2022" has state "verified" and fixtures 1
    And the pair "javascript/node-es2022 -> csharp/net10" has state "planned"
    And "docs/capabilities.md" contains the row "| net10 | planned | verified |" under the heading "csharp"

  @EndToEnd
  Scenario: The weakest fixture decides the pair
    Given a valid gate record for "csharp/net10 -> javascript/node-es2022" with fixture "add32" at "verified" and fixture "mul64" at "emitted"
    When I run "polypiler-tooling report capabilities --gates artifacts/gates"
    Then the pair "csharp/net10 -> javascript/node-es2022" has state "emitted" and fixtures 2

  @EndToEnd
  Scenario: A hand-raised cell fails the check
    Given a committed "docs/capabilities.json" in which "javascript/node-es2022 -> csharp/net10" is "verified"
    And no gate record for that pair
    When I run "polypiler-tooling report capabilities --gates artifacts/gates --check"
    Then stderr contains "error PLY0061: docs/capabilities.json: pair javascript/node-es2022 -> csharp/net10 is committed as 'verified' but the evidence gives 'planned'"
    And the exit code is 1
    And "docs/capabilities.json" is unchanged

  @EndToEnd
  Scenario: A gate record with a wrong evidence hash contributes nothing
    Given a gate record for "csharp/net10 -> javascript/node-es2022" claiming "verified" whose manifest file was altered after recording
    When I run "polypiler-tooling report capabilities --gates artifacts/gates"
    Then stderr contains "PLY0058"
    And the pair "csharp/net10 -> javascript/node-es2022" has state "planned"
    And the exit code is 1

  @EndToEnd
  Scenario: The same inputs give the same bytes
    Given the tree "mixed"
    When I run "polypiler-tooling report capabilities" twice into two output directories
    Then the two "capabilities.json" files have the same SHA-256
