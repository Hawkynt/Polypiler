@Layer.core
Feature: Core CI layer

  @Slow
  Scenario: The repository passes its own audit
    When I run "polypiler-tooling ci audit" in the repository
    Then the exit code is 0
    And the output ends with "7 categories, 0 problems" preceded by one line per layer file

  @Slow
  Scenario: A warning fails the build
    Given the project "tests/fixtures/infra/ci/core/warn/Warn.csproj" containing "int unused = 1;"
    When "dotnet build" runs on it with "-warnaserror"
    Then the exit code is not 0
    And the output contains "error CS0219"

  @Slow
  Scenario: An unformatted file fails the format check
    Given the project "tests/fixtures/infra/ci/core/unformatted/Unformatted.csproj" with a 4-space-indented class body
    When "dotnet format" runs on it with "--verify-no-changes"
    Then the exit code is not 0
    And the file on disk is unchanged

  Scenario Outline: The audit names what no job runs
    Given the audit tree "tests/fixtures/infra/ci/core/<tree>"
    When I run "polypiler-tooling ci audit --root tests/fixtures/infra/ci/core/<tree>"
    Then stderr contains "error <code>: <message>"
    And the exit code is 1

    Examples:
      | tree                    | code    | message                                                          |
      | audit-missing-job       | PLY0075 | layer 'rust' has no job 'layer-rust' in .github/workflows/ci.yml |
      | audit-unknown-toolchain | PLY0075 | layer 'rust' names unknown toolchain 'cargo'                     |
      | audit-unrun-category    | PLY0076 | category 'Performance' is not run by any step                    |
      | audit-untagged-test     | PLY0076 | test 'Demo.Tests.RunsNowhere' has no Layer category              |

  Scenario: Changed paths select layers, and core always runs
    Given layer files "core" (always), "rust" (paths "Polypiler.Core.Backends.Rust/**") and "js" (paths "Polypiler.Core.Backends.JavaScript/**")
    And the changed file list "Polypiler.Core.Backends.Rust/Emitter.cs" and "README.md"
    When I run "polypiler-tooling ci matrix --changed changed.txt"
    Then the output is '["core","rust"]'
