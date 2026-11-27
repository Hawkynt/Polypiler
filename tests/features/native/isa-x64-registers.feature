@Layer.core
Feature: x86-64 integer register profile

  Scenario: A doubleword write clears the upper half
    Given the x64 register "rax" holds "0x1122334455667788"
    When MachineIR writes "0x89ABCDEF" to the x64 register "eax"
    Then the x64 register "rax" holds "0x0000000089ABCDEF" before and after normalization

  Scenario: A high-byte write preserves adjacent bits
    Given the x64 register "rax" holds "0x1122334455667788"
    When MachineIR writes "0xAB" to the x64 register "ah"
    Then the x64 register "rax" holds "0x112233445566AB88" before and after normalization

  Scenario: An undeclared register is refused
    When MachineIR verifies a write to the x64 register "r16"
    Then the register diagnostic is "PLY4000"
