@Layer.core
Feature: Emit ordered standard output in C#

  Scenario: Branch and call writes preserve their order and return value
    Given the IR equivalent of the pinned OutputEffect.cs fixture
    When it is emitted to C# and the two compiled versions run with choose true
    Then both emit "first", a blank line, and "second" in order
    And both return 42

  Scenario: The false branch omits the blank line
    Given the IR equivalent of the pinned OutputEffect.cs fixture
    When it is emitted to C# and the two compiled versions run with choose false
    Then both emit "first" and "second" in order
    And both return 42

  Scenario: Earlier output remains when the next operand traps
    Given the IR equivalent of the pinned OutputEffect.cs fixture
    When the two compiled versions call Trap with divisor 0
    Then both emit only "first"
    And both raise DivideByZeroException

  Scenario: The verifier refuses a nonstring output operand
    Given an IR write of the Int32 value 42
    When the module is checked for C# emission
    Then it reports PLY0100 and no C# file is produced
