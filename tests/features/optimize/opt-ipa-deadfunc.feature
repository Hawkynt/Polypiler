@Layer.core
Feature: O0381 dead function elimination

  Scenario: A dead cycle is removed and the used function stays
    Given the fixture "cycle.pir": exported @main calls @Used; @Unused and @AlsoUnused call each other
    When the optimizer runs with only O0381 enabled
    Then the module contains exactly the functions @main and @Used
    And the opt-report contains "O0381 applied: @Unused removed" and "O0381 applied: @AlsoUnused removed"
    And interpreting @main in both versions records "@print(1)"

  Scenario: A helper inlined at every call site disappears
    Given the fixture "twice.pir": internal @Twice(%v) returns "Mul.i32[wrap] %v, 2" and is called twice from the exported @main
    When the pipeline runs at --opt balanced with only O0006 and O0381 enabled
    Then the module contains no function @Twice
    And interpreting @main in both versions returns the same value

  Scenario: A function reachable only through a code reference in a global is kept
    Given the fixture "table.pir": a global dispatch table initialized with "FuncRef @handler", and @main calling through the table
    When the optimizer runs with only O0381 enabled
    Then the IR is unchanged

  Scenario: Exported and test functions are roots
    Given the fixture "roots.pir": an exported @api that nothing calls, a test "roundtrip" that calls @helper, and an internal @orphan
    When the optimizer runs with only O0381 enabled
    Then the module contains @api, the test "roundtrip" and @helper, and no @orphan

  Scenario: The switch disables the rule
    Given the fixture "tests/fixtures/optimize/o0381/cycle.pir": the dead-cycle tree
    When the pipeline runs at --opt balanced with --disable-opt O0381
    Then the IR is unchanged
    And the opt-report does not mention O0381
