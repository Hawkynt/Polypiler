@Layer.core
Feature: MachineIR register and flag model

  Scenario: A byte sub-register write preserves the other bits of its root
    Given the MachineIR "machine test-m32\n@f { entry: %v = Const.u8 0xFF; RegWrite.u8 @r0l, %v; Ret }"
    When it is normalized to root registers
    Then the entry block is "%v = Const.u8 0xFF; %r0 = RegRead.u32 @r0; %v.z = ZeroExtend.u32 %v; %r0.1 = InsertBits.u32 %r0, %v.z, 0, 8; RegWrite.u32 @r0, %r0.1; Ret"
    And with r0 = 0x12345678 on entry the final r0 is 0x123456FF

  Scenario: A zero-extending sub-register write replaces the whole root
    Given the MachineIR "machine test-m64\n@f { entry: %v = Const.u32 1; RegWrite.u32 @r0d, %v; Ret }"
    When it is normalized to root registers
    Then the function contains "%v.z = ZeroExtend.u64 %v" and r0 is no input of it
    And with r0 = 0xFFFFFFFFFFFFFFFF on entry the final r0 is 0x1

  Scenario: Flags written on two paths are read in the join
    Given the MachineIR "machine test-m32\n@f {\nentry:\n  %k = RegRead.u32 @r1\n  %one = Eq.u32 %k, 1\n  Branch %one, a, b\na:\n  FlagWrite @c, 1\n  Jump join\nb:\n  FlagWrite @c, 0\n  Jump join\njoin:\n  %c = FlagRead.bool @c\n  %x = ZeroExtend.u32 %c\n  RegWrite.u32 @r0, %x\n  Ret\n}"
    When it is verified
    Then verification succeeds
    And with r1 = 0x1 on entry the final r0 is 0x1
    And with r1 = 0x0 on entry the final r0 is 0x0

  Scenario: An unknown register is rejected
    Given the MachineIR "machine test-m32\n@f { entry: %a = RegRead.u32 @r9; Ret }"
    When it is verified
    Then verification fails with PLY4000 and the message "'r9' is not a register or flag of machine profile 'test-m32'"

  Scenario: Reading a flag that is only undefined is rejected
    Given the MachineIR "machine test-m32\n@f { entry: FlagUndef @o; %x = FlagRead.bool @o; Ret }"
    When it is verified
    Then verification fails with PLY4002 and the message "flag 'o' is read at @f entry[1] but is undefined after @f entry[0]"
