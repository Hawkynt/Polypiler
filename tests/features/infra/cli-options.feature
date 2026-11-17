@Layer.core
Feature: Pipeline options

  The scenarios register the test-only command "options", which attaches the four pipeline options,
  knows the optimizations O0001 and O0100 and prints the canonical rendering of what it read, and
  run it in process.

  Scenario: Defaults
    When I run "polypiler options"
    Then the exit code is 0
    And stdout is "--opt balanced"

  Scenario: Equivalent command lines have one canonical form
    When I run "polypiler options --disable-opt O0100,O0001 --disable-opt O0100 --opt speed"
    Then the exit code is 0
    And stdout is "--opt speed --disable-opt O0001,O0100"

  Scenario: An assumption is accepted, normalized and reported
    When I run "polypiler options --assume 'Sum.n in [0x1,31]'"
    Then the exit code is 0
    And stdout is "--opt balanced --assume \"Sum.n in [1, 31]\""
    And stderr contains "warning PLY0009: assumption 'Sum.n in [1, 31]' is trusted without proof"

  Scenario: Strict mode refuses an unproven assumption
    When I run "polypiler options --strict --assume 'Sum.n in [1, 31]'"
    Then the exit code is 3
    And stderr contains "error PLY0010: strict mode: PLY0009 assumption 'Sum.n in [1, 31]' is trusted without proof"

  Scenario: An inverted range is refused
    When I run "polypiler options --assume 'Sum.n in [32, 31]'"
    Then the exit code is 2
    And stderr contains "error PLY0007: 'Sum.n in [32, 31]' is not a valid assumption: lower bound 32 exceeds upper bound 31"

  Scenario: An unknown optimization ID is refused
    When I run "polypiler options --disable-opt O0002"
    Then the exit code is 2
    And stderr contains "error PLY0008: unknown optimization 'O0002'"

  Scenario: A pipeline option before the command name is refused
    When I run "polypiler --opt speed options"
    Then the exit code is 2
    And stderr is "polypiler: error PLY0003: unknown option '--opt'"
    And stdout is empty

  Scenario: An empty item in --disable-opt is refused before the ID check
    When I run "polypiler options --disable-opt O0001,,O0002"
    Then the exit code is 2
    And stderr is "polypiler: error PLY0003: option '--disable-opt' has an empty list element"

  Scenario: An unknown level is refused
    When I run "polypiler options --opt fastest"
    Then the exit code is 2
    And stderr contains "error PLY0012: 'fastest' is not an optimization level; expected one of: none, balanced, speed, size, memory"
