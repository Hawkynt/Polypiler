Feature: Gate-state tracking per language pair

  Scenario Outline: A fixture's state is the end of its unbroken evidence chain
    Given a fixture with valid evidence for the states <evidence>
    When its gate state is computed
    Then the state is "<state>"

    Examples:
      | evidence                                                               | state     |
      | parsed                                                                 | parsed    |
      | parsed, lifted                                                         | lifted    |
      | parsed, lifted, legalized, emitted, compiled, executed, verified       | verified  |
      | parsed, lifted, recognized, proved, legalized                          | legalized |
      | parsed, lifted, legalized, emitted, executed                           | emitted   |
      | parsed, lifted, proved                                                 | lifted    |
      | lifted, legalized                                                      | planned   |

  Scenario: A pair is advertised at the lowest state among its fixtures
    Given the pair csharp to javascript with profiles "net10" and "node-es2022"
    And fixture "add32" recorded up to "verified" and fixture "mix32" recorded up to "compiled"
    When the gate store is loaded
    Then the pair state is "compiled"
    And the pair kotlin to perl with profiles "jvm" and "perl5" has state "planned"

  Scenario: Evidence whose hash no longer matches voids the record
    Given the record "tests/fixtures/verify/gates/invalid/hash-mismatch.json"
    When the gate store is loaded
    Then diagnostic PLY0058 is reported with reason "evidence hash differs"
    And the pair state is "planned"

  Scenario: A manifest that is only built cannot support the verified state
    Given a record whose "verified" evidence is a manifest with state "built"
    When the gate store is loaded
    Then diagnostic PLY0058 is reported with reason "manifest state 'built' is below 'verified'"
    And the pair state is "planned"

  Scenario: A lower state than the baseline is reported as a regression
    Given a baseline in which csharp to javascript is "verified"
    And a current store in which csharp to javascript is "compiled"
    When the current store is compared with the baseline
    Then exactly one regression is returned, from "verified" to "compiled"
