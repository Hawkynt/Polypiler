@Layer.basic
Feature: BASIC/PB CI layer

  PowerBASIC programs are compiled by PB-Compiler at its pinned commit and run in DOSBox-X, both
  through the sandboxed runner; the job layer-basic states which oracles actually ran.

  @EndToEnd
  Scenario: A PB-Compiler test program runs in the emulator and matches its expectation
    Given the source "tests/ARITH.BAS" of PB-Compiler at its pinned commit
    When it is compiled with "pb-compiler" and executed in "dosbox-x"
    Then the captured output equals PB-Compiler's "tests/ARITH.expected"
    And its first three lines are " 3", " 7" and " 42"

  @EndToEnd
  Scenario: Integer division and MOD truncate toward zero
    Given the source "tests/fixtures/infra/ci/basic/divmod.bas" printing "-7 \ 2" and "-7 MOD 2"
    When it is compiled with "pb-compiler" and executed in "dosbox-x"
    Then the captured output is "-3" followed by "-1"

  @EndToEnd
  Scenario: A syntax error is a target build failure
    Given the source "tests/fixtures/infra/ci/basic/syntax-error.bas"
    When it is compiled with "pb-compiler"
    Then the run outcome is "BuildFailed" with diagnostic "PLY0030"
    And no executable is started in the emulator

  @EndToEnd
  Scenario: A program that never ends is stopped at the time limit
    Given the source "tests/fixtures/infra/ci/basic/endless.bas" and a wall-time limit of 5 seconds
    When it is compiled with "pb-compiler" and executed in "dosbox-x"
    Then the run outcome is "TimedOut" with diagnostic "PLY0031"
    And no emulator process is left running

  Scenario: The genuine compiler is reported as not available in CI
    When the layer writes its oracle availability table
    Then the row for "powerbasic-genuine" reads "external"
    And the rows for "pb-compiler" and "dosbox-x" read "ci"
