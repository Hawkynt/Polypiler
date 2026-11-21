@Layer.core
Feature: Observable-state trace and comparison

  Scenario: Equal observed state compares equal although an unobserved register differs
    Given an expected trace with r0 = 0x00000005 and r1 = 0x00000001
    And an actual trace with r0 = 0x00000005 and r1 = 0x000000FF
    And a projection observing only the register r0
    When TraceComparer.Compare runs
    Then the diff is Equal

  Scenario: Swapped device writes are a difference at the first event
    Given an expected trace with the events device-write port 0x3C8 = 0x10 then device-write port 0x3C9 = 0x3F
    And an actual trace with the same two events in the opposite order
    And a projection observing the event kind "device"
    When TraceComparer.Compare runs
    Then the diff reports "event 0" with expected address 0x3C8 and actual address 0x3C9

  Scenario: Semantic mode ignores the order of memory writes but not the final bytes
    Given an expected trace whose final bytes at 0x1000 are 01 02 and an actual trace that wrote 0x1001 before 0x1000 with the same final bytes
    And a projection observing memory 0x1000 length 2 in mode Semantic
    When TraceComparer.Compare runs
    Then the diff is Equal

  Scenario: Raw mode reports the same pair as different
    Given an expected trace whose final bytes at 0x1000 are 01 02 and an actual trace that wrote 0x1001 before 0x1000 with the same final bytes
    And a projection observing "memory-write" events in mode Raw
    When TraceComparer.Compare runs
    Then the diff reports "event 0"

  Scenario: An undefined actual flag does not satisfy a defined expectation
    Given an expected trace with flag c = 1 and an actual trace with flag c undefined
    And a projection observing the flag c
    When TraceComparer.Compare runs
    Then the diff reports "flag c" with expected 1 and actual "undefined"

  Scenario: A vector file observing an unknown register is rejected
    Given a machine vector file for ISA "test-m32" whose observe.registers contains "eax"
    When MachineVectorFile.Load runs
    Then it fails with PLY4032 naming "eax" and "test-m32"
