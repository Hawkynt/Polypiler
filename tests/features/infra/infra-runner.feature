@OsIntegration
Feature: Sandboxed cross-language runner

  Scenario: A program is built, executed and compared
    Given the fixture project "hello" and the build step "dotnet build -c Release -o out"
    And the execute step "dotnet out/hello.dll"
    When the plan runs with default limits as trusted
    Then the outcome is Completed with exit code 0
    And stdout is exactly the 9 bytes "hello 42\n"
    And comparing stdout with the expected "hello 42\n" in Exact mode yields Equal
    And the working directory no longer exists

  Scenario: A failing build is reported and nothing is executed
    Given the fixture project "broken" and the build step "dotnet build -c Release -o out"
    When the plan runs with default limits as trusted
    Then the outcome is BuildFailed
    And the diagnostics contain exactly one error with code "PLY0030"
    And stdout of the execute step is empty

  Scenario Outline: A limit violation kills the process tree
    Given the probe invoked as "<probe arguments>"
    And the limits wall time <wall> ms, memory <memory> MiB, output <output> bytes
    When the plan runs as trusted
    Then the outcome is <outcome>
    And the diagnostics contain an error with code "<code>"
    And no process of the run is alive afterwards

    Examples:
      | probe arguments | wall  | memory | output  | outcome        | code    |
      | sleep 5000      | 500   | 1024   | 1048576 | TimedOut       | PLY0031 |
      | alloc 512       | 60000 | 128    | 1048576 | MemoryExceeded | PLY0032 |
      | spew 2097152    | 60000 | 1024   | 1048576 | OutputExceeded | PLY0033 |

  Scenario: Output is truncated at the limit
    Given the probe invoked as "spew 2097152" with an output limit of 1048576 bytes
    When the plan runs as trusted
    Then the captured stdout has exactly 1048576 bytes

  Scenario: An isolated run has no network and cannot write outside its directory
    Given a host with isolation level Isolated
    And a TCP listener on 127.0.0.1 opened by the test on a free port
    When the probe runs "connect 127.0.0.1" with that port and then "write" with a path in the caller's home directory
    Then both probe runs exit with code 1
    And the listener has accepted 0 connections
    And the file in the caller's home directory does not exist

  Scenario: Untrusted input is refused where it cannot be isolated
    Given a host with isolation level Limited
    When a plan with the probe invoked as "echo" runs as untrusted
    Then the outcome is Refused
    And the diagnostics contain an error with code "PLY0034"
    And no process was started

  Scenario: A differing output is located
    Given the expected output "hello 42\n" and the actual output "hello 43\n"
    When they are compared in Exact mode
    Then the first difference is at byte offset 7 with expected 0x32 and actual 0x33
