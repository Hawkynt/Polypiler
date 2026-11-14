@ExternalInterop
Feature: Audit of the PB-Compiler LLVM emitter

  Scenario: A module pbc emits is accepted by the LLVM assembler and verifier
    Given the pinned pb-compiler and a fixture whose characterization entry says outcome "emitted"
    When I run "pbc --emit-llvm" on that fixture
    Then the output contains a line starting with "target triple"
    And llvm-as exits with 0 for that output
    And "opt -passes=verify" exits with 0 for that output

  Scenario: Every access in emitted modules is byte-aligned
    Given the modules emitted for all fixtures whose characterization outcome is "emitted"
    When their load and store instructions are listed
    Then each of them ends with "align 1"

  Scenario: A declined program names the construct and yields no module
    Given a PowerBASIC program "DIM gp AS DWORD : gp = CODEPTR32(S6) : GOTO DWORD gp" with "SUB S6() : END SUB"
    When I run "pbc --emit-llvm" on it
    Then the exit code is 1
    And stderr contains "--emit-llvm"
    And stdout contains no "define"

  Scenario: The audit covers every instruction arm of the emitter
    Given the pinned file "PowerBasic.Compiler/Ir/LlvmEmitter.cs"
    When the IR instruction types named in its EmitInstruction switch are listed
    Then each of them has a row in table 1 of "docs/llvm/pb-emitter-audit.md"

  Scenario: A changed outcome is detected
    Given a characterization entry edited from "emitted" to "declined"
    When the characterization tests run
    Then they fail naming that fixture and the recorded and actual outcome
