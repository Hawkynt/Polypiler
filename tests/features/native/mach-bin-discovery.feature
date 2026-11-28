@Layer.core
Feature: Function and instruction discovery

  Recursive traversal on the toy ISA: 00 nop, 01 return, 02 rr jump, 03 rr conditional jump, 04 rr
  call, 05 indirect jump, 06 trap, 07 nn with nn table bytes an inline jump table, FF invalid.

  Scenario: Straight-line code and a call yield two functions
    Given the toy image "04 02 01 FF 00 01" loaded at 0x1000 with the entry 0x1000
    When Discovery.Run executes
    Then function 0x1000 is Proven with one block 0x1000..0x1002
    And function 0x1004 is Derived with evidence "call from 0x1000" and one block 0x1004..0x1005
    And byte 0x1003 is Undecoded

  Scenario: Both arms of a conditional jump are followed
    Given the toy image "03 01 01 00 01" at 0x1000 with the entry 0x1000
    When Discovery.Run executes
    Then function 0x1000 has the blocks 0x1000..0x1001, 0x1002..0x1002 and 0x1003..0x1004
    And every byte is Code

  Scenario: An unresolved indirect jump leaves the bytes behind it undecoded
    Given the toy image "05 00 00 01" at 0x1000 with the entry 0x1000
    When Discovery.Run executes
    Then the only block ends in an indirect jump marked unresolved
    And bytes 0x1001..0x1003 are Undecoded
    And the diagnostics contain PLY4070 naming 0x1000

  Scenario: A validated inline jump table adds its targets and marks the table as data
    Given the toy image "07 02 02 03 00 01 01 01" at 0x1000 with the entry 0x1000
    When Discovery.Run executes with the toy jump-table resolver
    Then the indirect jump has the targets 0x1006 and 0x1007
    And bytes 0x1002..0x1003 are Data
    And bytes 0x1004..0x1005 are Undecoded

  Scenario: Overlapping instructions are reported, not chosen between
    Given the toy image "02 00 02 00 01" at 0x1000 with the entries 0x1000 and 0x1003
    When Discovery.Run executes
    Then byte 0x1003 is Conflict and the diagnostics contain PLY4072 naming 0x1002 and 0x1003

  Scenario: An invalid opcode reached by control flow makes the function incomplete
    Given the toy image "00 FF 01" at 0x1000 with the entry 0x1000
    When Discovery.Run executes
    Then function 0x1000 is not Complete and the diagnostics contain PLY4071 naming 0x1001
