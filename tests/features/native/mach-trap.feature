@Layer.core
Feature: Machine traps

  Scenario: A software interrupt with a service contract reads and writes only its registers
    Given the MachineIR "machine test-m32\n@f { entry: MachineTrap[kind=software-interrupt, vector=0x21, resumes=true, id=t1] service(\"svc.put\") reads(r0) writes(r1); Ret }"
    When it is verified
    Then verification succeeds
    And the trap is on channel "Trap" and reads register r0 and writes register r1
    And the trap interferes with "RegWrite.u32 @r2, 5": false
    And the trap interferes with "RegWrite.u32 @r0, 5": true

  Scenario: A software interrupt without a contract is conservative
    Given the MachineIR "machine test-m32\n@f { entry: MachineTrap[kind=software-interrupt, vector=0x10, resumes=true, id=t2]; Ret }"
    When it is verified
    Then the diagnostics are the warning PLY4026 "software interrupt 0x10 at @f entry[0] has no service contract; all registers and unknown memory are assumed read and written"
    And the trap interferes with "%l = Load.u8 0x1000": true

  Scenario: An undefined-instruction trap terminates its block
    Given the MachineIR "machine test-m32\n@f { entry: MachineTrap[kind=undefined-instruction, vector=6, resumes=false, id=t3] }"
    When it is verified
    Then verification succeeds
    And the trap ends its block and has no successors

  Scenario: An instruction after a non-resuming trap is rejected
    Given the MachineIR "machine test-m32\n@f { entry: MachineTrap[kind=undefined-instruction, vector=6, resumes=false, id=t3]; %x = Add.u32 1, 2; Ret }"
    When it is verified
    Then verification fails with PLY4005 and the message "'@f' is not well-formed MachineIR: an instruction follows the terminator of the block 'entry' at @f entry[1]"

  Scenario: A vector outside the profile's range is rejected
    Given the MachineIR "machine test-m32\n@f { entry: MachineTrap[kind=software-interrupt, vector=256, resumes=true, id=t4] service(\"x\"); Ret }"
    When it is verified
    Then verification fails with PLY4025 and the message "interrupt vector 256 is outside 0..255 for machine profile 'test-m32'"
