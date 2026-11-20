@ExternalInterop @Layer.core
Feature: polypiler verify

  Every scenario works on a temporary copy of its fixture directory, "copy" in the workspace, which
  holds tests/fixtures/cli/verify as well; node runs the generated module.

  Scenario: A generated directory is verified against independent vectors
    Given a copy of "tests/fixtures/cli/verify/adler32-js" whose manifest state is "generated"
    When I run "polypiler verify copy --against tests/fixtures/cli/verify/adler32.vectors.json"
    Then the exit code is 0
    And the last line of stdout is "copy: verified (4 of 4 vectors)"
    And the manifest state is "verified"

  Scenario: A mismatch downgrades the manifest and reports both values
    Given a copy of "tests/fixtures/cli/verify/adler32-js" whose manifest state has been set to "verified"
    When I run "polypiler verify copy --against tests/fixtures/cli/verify/adler32.wrong.vectors.json"
    Then the exit code is 5
    And stdout contains "FAIL expected 0x024D0128 actual 0x024D0127"
    And stderr contains "error PLY0051"
    And the manifest state is "executed" with verification result "failed"

  Scenario: --no-update leaves the directory untouched
    Given a copy of "tests/fixtures/cli/verify/adler32-js"
    When I run "polypiler verify copy --against tests/fixtures/cli/verify/adler32.vectors.json --no-update"
    Then the exit code is 0
    And every file in the copy has the SHA-256 it had before the run

  Scenario: Edited generated code is not verified
    Given a copy of "tests/fixtures/cli/verify/adler32-edited"
    When I run "polypiler verify copy --against tests/fixtures/cli/verify/adler32.vectors.json"
    Then the exit code is 1
    And stderr contains "error PLY0054"
    And nothing was built or executed

  Scenario: A directory without a manifest is refused
    When I run "polypiler verify tests/fixtures/cli/verify/no-manifest --against tests/fixtures/cli/verify/adler32.vectors.json"
    Then the exit code is 1
    And stderr contains "error PLY0016: no manifest in 'tests/fixtures/cli/verify/no-manifest'"

  Scenario: A malformed vector file is a usage error
    Given a copy of "tests/fixtures/cli/verify/adler32-js"
    When I run "polypiler verify copy --against tests/fixtures/cli/verify/broken.json"
    Then the exit code is 2
    And stderr contains "error PLY0017: 'tests/fixtures/cli/verify/broken.json' holds no test vectors"
    And the manifest state is still "generated"
