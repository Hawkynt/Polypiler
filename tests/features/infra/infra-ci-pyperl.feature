@Layer.pyperl
Feature: Python/Perl CI layer

  The pinned interpreters of tooling/toolchains.lock run the smoke fixtures of
  tests/fixtures/infra/ci/pyperl in the sandbox of the cross-language runner. Python runs with -I, so
  the working directory is not on sys.path; unittest finds the test module there through
  "discover -s .". prove is the App::Prove program of the core distribution, started by perl.

  Scenario Outline: The smoke script prints the checksum
    Given the script "tests/fixtures/infra/ci/pyperl/<script>"
    When I run it with "<command>" in the sandbox
    Then stdout is "38600999"
    And the exit code is 0

    Examples:
      | script     | command      |
      | adler32.py | python -I -B |
      | adler32.pl | perl -I.     |

  Scenario: The module tests pass under each language's standard runner
    Given the Python test "test_adler32.py" and the Perl test "adler32.t"
    When I run "python -I -B -m unittest discover -v -s . -p test_adler32.py" and "prove -I. adler32.t" in the sandbox
    Then the first command reports "OK" after "Ran 1 test"
    And the second command reports "All tests successful."

  Scenario: Integers beyond 64 bits behave as each language defines
    When I run "bigint.py" and "bigint.pl" in the sandbox
    Then "bigint.py" prints "18446744073709551616"
    And "bigint.pl" prints "1.84467440737096e+19" followed by "18446744073709551616"

  Scenario Outline: A failing test or a syntax error fails the run
    When I run "<command>" in the sandbox
    Then the exit code is <exit>
    And the output contains "<text>"

    Examples:
      | command                                                   | exit | text                |
      | python -I -B -m unittest discover -s . -p failing_test.py | 1    | FAILED (failures=1) |
      | python -I -B syntax_error.py                              | 1    | SyntaxError         |
      | perl -c syntax-error.pl                                   | 255  | syntax error        |
