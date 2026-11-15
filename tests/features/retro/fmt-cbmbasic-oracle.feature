@ExternalInterop @Layer.core
Feature: VICE and petcat oracles for Commodore BASIC

  Scenario: A program's screen output is captured
    Given the PRG "hello.prg" with the bytes "01 08 0C 08 0A 00 99 20 22 48 49 22 00 00 00"
    When it is run on the machine C64
    Then the status is ReturnedToBasic
    And the screen text is "HI"

  Scenario: Memory can be read after the run
    Given the PRG "poke.prg" containing the line "10 POKE 53280,1" and the range 0xD020 of length 1 to read
    When it is run on the machine C64
    Then the low nibble of the byte read at 0xD020 is 0x1

  Scenario: The genuine ROM and memory size are in use
    When an empty program is run on the machine C64 with the start-up banner captured
    Then the banner contains "38911 BASIC BYTES FREE"

  Scenario: petcat lists and tokenizes independently
    Given the PRG "hello.prg"
    When petcat detokenizes it for dialect "c64-basic-v2"
    Then the normalised listing is '10 PRINT "HI"'
    And petcat tokenizing that listing with load address 0x0801 yields the bytes of "hello.prg"

  Scenario: A program that never ends is reported as a cycle limit
    Given the PRG "forever.prg" containing "10 GOTO 10" and a cycle limit of 20000000
    When it is run on the machine C64
    Then the status is CycleLimit
    And no ReturnedToBasic result is reported

  Scenario: A corrupt PRG fails as an oracle failure, not as a pass
    Given a PRG consisting of the bytes "01 08 FF"
    When it is run on the machine C64 with ReturnedToBasic required
    Then the diagnostic PLY3935 is reported
