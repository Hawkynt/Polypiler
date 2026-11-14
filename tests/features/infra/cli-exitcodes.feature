Feature: Stable exit codes

  The scenarios register the test-only command "raise" beside "echo" and "demand" and run them in
  process. "raise" reports the codes named on its command line, in order, each with the severity
  and exit code spec/diagnostics.yaml registers for it, and can throw or return a number of its own.
  The numbers follow ADR 0003: 3 is an unsupported construct, 4 invalid source.

  Scenario Outline: Each failure category has its own exit code
    When I run "polypiler raise <code>"
    Then the exit code is <exit>
    And stderr contains "error <code>:"

    Examples:
      | code    | exit |
      | PLY0004 | 1    |
      | PLY0003 | 2    |
      | PLY0014 | 3    |
      | PLY0013 | 4    |
      | PLY0051 | 5    |
      | PLY0030 | 6    |
      | PLY0050 | 7    |
      | PLY0024 | 8    |
      | PLY0081 | 9    |
      | PLY0031 | 10   |
      | PLY0001 | 70   |

  Scenario: No diagnostic is success without a tally line
    When I run "polypiler raise"
    Then the exit code is 0
    And stderr is empty

  Scenario: A warning alone is success
    When I run "polypiler raise PLY0009"
    Then the exit code is 0
    And the last line of stderr is "polypiler: 0 error(s), 1 warning(s)"

  Scenario: The first error wins over later ones
    When I run "polypiler raise PLY0009 PLY0030 PLY0051"
    Then the exit code is 6
    And the last line of stderr is "polypiler: 2 error(s), 1 warning(s)"

  Scenario: An internal error wins wherever it comes
    When I run "polypiler raise PLY0030 PLY0001"
    Then the exit code is 70

  Scenario: With JSON diagnostics no tally line is written
    When I run "polypiler --diagnostics json raise PLY0009 PLY0030"
    Then the exit code is 6
    And stderr is 2 lines, each one JSON object
    And stderr does not contain "error(s)"

  Scenario: With --quiet only the error is written
    When I run "polypiler --quiet raise PLY0009 PLY0030"
    Then the exit code is 6
    And stderr is "polypiler: error PLY0030: target build failed: '<toolchain> <arguments>' exited with <code>"

  Scenario: A refused command line has no tally line
    When I run "polypiler frobnicate"
    Then the exit code is 2
    And stderr is "polypiler: error PLY0002: unknown command 'frobnicate'; run 'polypiler --help'"

  Scenario: A usage error of a command that ran has a tally line
    When I run "polypiler raise PLY0003"
    Then the exit code is 2
    And the last line of stderr is "polypiler: 1 error(s), 0 warning(s)"

  Scenario: An escaping exception is an internal error
    When I run "polypiler raise --throw InvalidOperationException:boom"
    Then the exit code is 70
    And stderr contains "polypiler: error PLY0001: internal compiler error: InvalidOperationException: boom"
    And stdout is empty

  Scenario: A cancelled run exits with 130 and reports nothing
    When I run "polypiler raise --throw OperationCanceledException"
    Then the exit code is 130
    And stderr is empty

  Scenario: A command that fails silently is not trusted
    When I run "polypiler raise --return 3"
    Then the exit code is 70
    And stderr contains "error PLY0001: command 'raise' returned 3 without a diagnostic"

  Scenario: The table is available from the tool
    When I run "polypiler help exit-codes"
    Then the exit code is 0
    And stdout line 1 is "  0  success; verification passed if it was requested"
    And stdout contains the line "  3  a construct is outside the supported subset"
    And stdout contains the line "  4  the source is not valid in its language"
    And stdout contains the line " 70  internal compiler error"
