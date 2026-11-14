Feature: PC-BASIC oracle for GW-BASIC token files

  PC-BASIC (tooling/toolchains.lock key "pcbasic") lists, tokenizes and runs GW-BASIC token files as an
  external process. Without it at its pinned version every scenario is inconclusive, and fails with
  PLY0021 or PLY0022 when POLYPILER_REQUIRE_TOOLCHAINS=1. GW-BASIC prints a space before a positive
  number and one after every number (GW-BASIC User's Guide, PRINT statement), so "PRINT 1" prints " 1 ".

  @ExternalInterop
  Scenario: A token file is listed
    Given the token file "print1.bas" with the bytes "FF 09 00 0A 00 91 20 12 00 00 00 1A"
    When PC-BASIC converts it to ASCII
    Then the listing is "10 PRINT 1"

  @ExternalInterop
  Scenario: A listing is tokenized
    Given the listing "10 PRINT 1"
    When PC-BASIC converts it to a token file
    Then the bytes from offset 3 are "0A 00 91 20 12 00 00 00 1A" and the first byte is FF

  @ExternalInterop
  Scenario: A program is run and its output captured
    Given the token file "print1.bas"
    When PC-BASIC runs it
    Then the output is " 1 "

  @ExternalInterop
  Scenario: An endless program is an oracle failure
    Given the token file "forever.bas" containing "10 GOTO 10" and a limit of 5 seconds
    When PC-BASIC runs it
    Then the outcome is TimedOut and the diagnostic PLY0031 is reported
    And no output is returned as a result
