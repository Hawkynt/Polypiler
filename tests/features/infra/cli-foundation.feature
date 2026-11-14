Feature: Command-line foundation

  The scenarios register the test-only commands "echo" and "demand" beside nothing else and run
  them in process, in a workspace that holds the fixtures of tests/fixtures/cli/foundation.

  Scenario: Root help lists the registered commands
    When I run "polypiler --help"
    Then the exit code is 0
    And stdout line 2 is "usage: polypiler <command> [options]"
    And stdout contains a line starting with "  echo  "
    And stderr is empty

  Scenario Outline: Both value spellings are accepted
    When I run "polypiler echo tests/fixtures/cli/foundation/input.txt <spelling>"
    Then the exit code is 0
    And stdout is "echo tests/fixtures/cli/foundation/input.txt"

    Examples:
      | spelling |
      | --mode a |
      | --mode=a |

  Scenario Outline: A global option is accepted before and after the command name
    When I run "polypiler <line>"
    Then the exit code is 0
    And stdout is "echo tests/fixtures/cli/foundation/input.txt"

    Examples:
      | line                                                            |
      | --quiet echo tests/fixtures/cli/foundation/input.txt            |
      | echo tests/fixtures/cli/foundation/input.txt --quiet            |
      | --diagnostics json echo tests/fixtures/cli/foundation/input.txt |
      | echo tests/fixtures/cli/foundation/input.txt --diagnostics=json |

  Scenario: A command option before the command name is refused
    When I run "polypiler --mode a echo tests/fixtures/cli/foundation/input.txt"
    Then the exit code is 2
    And stderr is "polypiler: error PLY0003: unknown option '--mode'"
    And stdout is empty

  Scenario: An unknown command is refused
    When I run "polypiler frobnicate"
    Then the exit code is 2
    And stderr is "polypiler: error PLY0002: unknown command 'frobnicate'; run 'polypiler --help'"
    And stdout is empty

  Scenario Outline: A command line that breaks the grammar is refused with one diagnostic
    When I run "polypiler <line>"
    Then the exit code is 2
    And stderr is "polypiler: error PLY0003: <message>"
    And stdout is empty
    And the command was not executed

    Examples:
      | line                             | message                                     |
      | echo input.txt --force=true      | option '--force' does not take a value      |
      | echo input.txt --force true      | unexpected argument 'true'                  |
      | echo input.txt --force --force   | option '--force' given more than once       |
      | echo input.txt --mode a --mode b | option '--mode' given more than once        |
      | demand                           | option '--key' is required                  |
      | echo --mode a                    | missing argument <input>                    |
      | echo input.txt other.txt         | unexpected argument 'other.txt'             |
      | echo input.txt --export a,,b     | option '--export' has an empty list element |
      | echo input.txt --export a,       | option '--export' has an empty list element |
      | echo input.txt --export=         | option '--export' has an empty list element |
      | --quiet                          | no command given                            |
      | echo input.txt --note -x         | option '--note' requires a value            |
      | echo input.txt --output:out      | unknown option '--output:out'               |
      | echo input.txt --output --force  | option '--output' requires a value          |
      | echo --nope                      | unknown option '--nope'                     |
      | echo input.txt -hq               | unknown option '-hq'                        |
      | -?                               | unknown option '-?'                         |
      | --version extra                  | unknown option '--version'                  |
      | help echo extra                  | unexpected argument 'extra'                 |

  Scenario Outline: A value that starts with "-" or is empty is accepted when attached
    When I run "polypiler echo tests/fixtures/cli/foundation/input.txt <spelling>"
    Then the exit code is 0
    And stdout is "echo tests/fixtures/cli/foundation/input.txt"

    Examples:
      | spelling  |
      | --note=-x |
      | --note=   |
      | --note '' |

  Scenario: A diagnostic carries the invocation as typed, quoted
    When I run "polypiler" with the arguments [--diagnostics=json] [echo] [my file.txt] [--mode] [c]
    Then the exit code is 2
    And stderr is one JSON object with "code" "PLY0003" and "command" "polypiler --diagnostics=json echo \"my file.txt\" --mode c"

  Scenario: A response-file spelling is an ordinary operand
    When I run "polypiler echo @tests/fixtures/cli/foundation/input.txt"
    Then the exit code is 1
    And stderr contains "error PLY0004: input '@tests/fixtures/cli/foundation/input.txt' does not exist or is not readable"

  Scenario Outline: Help takes precedence over everything else on the line
    When I run "polypiler <line>"
    Then the exit code is 0
    And stdout equals the stdout of "polypiler <same as>"
    And stderr is empty

    Examples:
      | line                             | same as     |
      | help echo                        | echo --help |
      | echo --nope --help               | echo --help |
      | --nope echo -h                   | echo --help |
      | --diagnostics json echo x --help | echo --help |
      | echo --mode --help               | echo --help |
      | help                             | --help      |
      | --nope --help                    | --help      |
      | --quiet -h                       | --help      |

  Scenario Outline: Help for an unknown command is refused
    When I run "polypiler <line>"
    Then the exit code is 2
    And stderr is "polypiler: error PLY0002: unknown command 'frobnicate'; run 'polypiler --help'"

    Examples:
      | line              |
      | frobnicate --help |
      | help frobnicate   |

  Scenario: The version is printed only for "--version" alone
    When I run "polypiler --version"
    Then the exit code is 0
    And stdout is one line holding the version of polypiler
    And stderr is empty

  Scenario: --quiet drops warnings and leaves stdout and the exit code alone
    Given the command "echo" also reports a test warning and a test error with exit code 1
    When I run "polypiler echo tests/fixtures/cli/foundation/input.txt --quiet"
    Then the exit code is 1
    And stdout is "echo tests/fixtures/cli/foundation/input.txt"
    And stderr holds the error line and no warning line

  Scenario: --diagnostics json leaves only JSON on stderr
    Given the command "echo" also reports a test warning and a test error with exit code 1
    When I run "polypiler --diagnostics json echo tests/fixtures/cli/foundation/input.txt"
    Then the exit code is 1
    And every line of stderr is one JSON object
    And stdout is "echo tests/fixtures/cli/foundation/input.txt"

  Scenario: An invalid option value is refused as JSON
    When I run "polypiler --diagnostics json echo tests/fixtures/cli/foundation/input.txt --mode c"
    Then the exit code is 2
    And stderr is one JSON object with "code" "PLY0003" and "message" "'c' is not a valid value for '--mode'; expected one of: a, b"
    And the command was not executed

  Scenario: A missing input file is refused
    When I run "polypiler echo tests/fixtures/cli/foundation/absent.txt"
    Then the exit code is 1
    And stderr contains "error PLY0004: input 'tests/fixtures/cli/foundation/absent.txt' does not exist or is not readable"

  Scenario: A non-empty output directory needs --force
    When I run "polypiler echo tests/fixtures/cli/foundation/input.txt --output tests/fixtures/cli/foundation/nonempty"
    Then the exit code is 1
    And stderr contains "error PLY0005: output directory 'tests/fixtures/cli/foundation/nonempty' is not empty; pass --force to write into it"
    And "tests/fixtures/cli/foundation/nonempty/keep.txt" is unchanged

  Scenario: Exclusive options are refused
    When I run "polypiler echo tests/fixtures/cli/foundation/input.txt --left --right"
    Then the exit code is 2
    And stderr contains "error PLY0011: options '--left' and '--right' cannot be combined"
