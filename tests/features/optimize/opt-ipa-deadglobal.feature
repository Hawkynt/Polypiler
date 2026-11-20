@Layer.core
Feature: O0383 dead global elimination

  Scenario: Dead data releases its dispatch functions
    Given the O0383 fixture "tables.pir"
    When the global optimizer enables "O0381,O0383"
    Then exactly globals "limit" and functions "main" remain
    And both loaded modules return 320 from main
    And the global report lists "table,ops"

  Scenario: Existing functions protect their data without the function sweep
    Given the O0383 fixture "tables.pir"
    When the global optimizer enables "O0383"
    Then exactly globals "limit,table" and functions "main,Unused,factory,area" remain
    And both loaded modules return 320 from main

  Scenario: A live initializer keeps its earlier dependency
    Given the O0383 fixture "nested.pir"
    When the global optimizer enables "O0381,O0383"
    Then the global module is unchanged
    And both loaded modules return 7 from main

  Scenario Outline: A host retains external or device storage
    Given the O0383 fixture "external.pir"
    And the host retains global "<name>"
    When the global optimizer enables "O0381,O0383"
    Then exactly globals "<name>" and functions "main" remain

    Examples:
      | name     |
      | external |
      | device   |

  Scenario: Disabling the global sweep retains initializer roots
    Given the O0383 fixture "disabled.pir"
    When the global optimizer enables "O0381"
    Then the global module is unchanged
    And the global report lists ""

  Scenario: An unread initializer still traps when the module loads
    Given the O0383 fixture "trap.pir"
    When the global optimizer enables "O0381,O0383"
    Then the global module is unchanged
    And loading either module traps with division by zero
