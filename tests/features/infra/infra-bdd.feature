Feature: Feature files are bound to NUnit

  Scenario: A command line runs in-process and its result is observable
    When I run "polypiler --version"
    Then the exit code is 0
    And stderr is empty

  Scenario: A refused command line reports its exit code and message
    When I run "polypiler frobnicate"
    Then the exit code is 2
    And stderr contains "unknown command 'frobnicate'"

  Scenario: Each scenario gets its own workspace
    Given the file "notes/a.txt" with content "one\ntwo"
    Then the file "notes/a.txt" exists
    And the file "notes/a.txt" contains "two"
    And the file "notes/b.txt" does not exist

  Scenario Outline: Titles become stable slugs
    Given the scenario title "<title>"
    When its slug is computed
    Then the slug is "<slug>"

    Examples:
      | title                                              | slug                                             |
      | A shift/or pair becomes a rotate                   | a-shift-or-pair-becomes-a-rotate                 |
      | O0100 recognize rotate-left                        | o0100-recognize-rotate-left                      |
      | --opt none  vs. --opt speed!                       | opt-none-vs-opt-speed                            |

  Scenario: A step without a binding fails the static check
    Given the feature file "tests/fixtures/infra/bdd/unbound.feature" whose line 4 is "When the moon is full"
    When the binding check runs over it
    Then it reports "PLY0074: tests/fixtures/infra/bdd/unbound.feature(4): step 'When the moon is full' has no binding"

  Scenario: An ambiguous step fails the static check
    Given the feature file "tests/fixtures/infra/bdd/ambiguous.feature" and two bindings matching "a doubled step"
    When the binding check runs over it
    Then it reports exactly one problem containing "has 2 bindings"

  Scenario: Every scenario in tests/features is a generated NUnit test
    When the scenarios in "tests/features" are counted with the Gherkin parser
    Then the count equals the number of Reqnroll-generated test cases in Polypiler.Tests.Acceptance
    And "infra-modules.feature" contributes at least 1 of them
