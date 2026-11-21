@Layer.core
Feature: Machine-state interpreter

  Scenario: Add with carry-out sets result and flags
    Given the routine "addc" with r0 = 0xFFFFFFFF and r1 = 0x00000001
    When MachineInterpreter.Run executes it
    Then the final r0 is 0x00000000
    And the final flags are c = 1, z = 1, s = 0, o = 0
    And the result equals the gcc reference for that vector

  Scenario: Signed overflow is visible in the overflow flag only
    Given the routine "addc" with r0 = 0x7FFFFFFF and r1 = 0x00000001
    When MachineInterpreter.Run executes it
    Then the final r0 is 0x80000000
    And the final flags are c = 0, z = 0, s = 1, o = 1
    And the result equals the gcc reference for that vector

  Scenario: A buffer sum reads memory and returns the total
    Given the routine "bytesum" with memory 0x1000 = 01 02 03 FF, r0 = 0x00001000 and r1 = 0x00000004
    When MachineInterpreter.Run executes it
    Then the final r0 is 0x00000105
    And the trace contains no memory-write event

  Scenario: A store before a trap is kept and the trap ends the run
    Given the routine "ud"
    When MachineInterpreter.Run executes it
    Then the outcome is Trapped, the final byte at 0x2000 is 0xAA and the last event is trap "undefined-instruction"

  Scenario: Reading an undefined flag aborts
    Given the MachineIR "machine test-m32\n@f {\nentry:\n  FlagUndef @c\n  Jump join\njoin:\n  %x = FlagRead.bool @c\n  Ret\n}"
    When MachineInterpreter.Run executes "@f"
    Then the outcome is Aborted with PLY4035 naming flag "c"
