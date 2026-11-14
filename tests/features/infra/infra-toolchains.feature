@EndToEnd
Feature: Pinned toolchains

  Scenario: The repository lock is valid and complete
    When I run "polypiler-tooling toolchains validate"
    Then the exit code is 0
    And stderr is empty
    And the lock contains every one of the 26 toolchain keys named in "backlog/README.md"
    And the keys include "dotnet-sdk", "node", "rust", "pb-compiler", "z3" and "vice"

  Scenario: An installed tool with the pinned version probes as Ok
    Given a lock entry "fake" with version "1.2.3", probe "fake-tool 'fake 1.2.3'" and versionPattern "^fake (?<version>\S+)$"
    When I run "polypiler-tooling toolchains probe --only fake"
    Then the output is "fake Ok expected=1.2.3 actual=1.2.3" followed by the tool's path
    And the exit code is 0

  Scenario: A different installed version is a mismatch
    Given a lock entry "fake" with version "1.2.3", probe "fake-tool 'fake 1.2.4'" and versionPattern "^fake (?<version>\S+)$"
    When I run "polypiler-tooling toolchains probe --only fake"
    Then stderr contains "error PLY0022: toolchain 'fake' is 1.2.4, the lock pins 1.2.3"
    And the exit code is 1

  Scenario: A tool that is not installed is reported, not skipped
    Given a lock entry "ghost" with probe "no-such-binary-polypiler --version"
    When I run "polypiler-tooling toolchains probe --only ghost"
    Then stderr contains "error PLY0021: toolchain 'ghost' is not installed"
    And stdout contains "ghost Missing "
    And the exit code is 1

  Scenario Outline: A malformed lock is refused with its reason
    Given the lock fixture "<fixture>"
    When I run "polypiler-tooling toolchains validate --lock <fixture>"
    Then stderr contains "error PLY0020: <reason>"
    And the exit code is 1

    Examples:
      | fixture               | reason                                             |
      | no-version.lock       | missing property 'version' in 'node'               |
      | extra-property.lock   | unknown property 'channel' in 'node'               |
      | no-group.lock         | versionPattern of 'node' has no group 'version'    |
      | unsorted.lock         | keys are not in ordinal order: 'gcc' after 'node'  |
      | schema-2.lock         | unsupported schema 2                               |

  Scenario: A test that requires an absent toolchain fails in CI
    Given the environment variable POLYPILER_REQUIRE_TOOLCHAINS is "1"
    And a test marked RequiresToolchain("ghost")
    When the test runs
    Then its outcome is Failed with message containing "PLY0021"
