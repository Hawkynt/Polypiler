@Layer.basic @ExternalInterop
Feature: Emit MS-DOS 6.22 scalar Batch

  Scenario: Addition wraps at the signed boundary
    Given a C# kernel adds one to an initialized Int32.MaxValue local
    When it is translated to dos622 and run by the pinned COMMAND.COM
    Then the DOS result record is PLYRESULT=80000000
    And interpreting those bits as Int32 yields the compiled C# return value

  Scenario: Checked overflow is refused
    Given the corresponding C# kernel uses checked addition
    When it is translated to dos622
    Then it reports an unsupported operation and writes no batch file

  Scenario Outline: A DOS kernel preserves the scalar result
    Given the closed C# DOS kernel named "<kernel>"
    When it is translated to dos622 and run by the pinned COMMAND.COM
    Then the DOS result record is PLYRESULT=<bits>
    And interpreting those bits as Int32 yields the compiled C# return value

    Examples:
      | kernel             | bits     |
      | ordinary addition  | 0000002A |
      | carry out          | 00000000 |
      | mixed signs        | FFFFFFD4 |
      | nested operands    | 00000111 |
      | loops and branches | 00000008 |
      | unequal branch     | 0000002A |
