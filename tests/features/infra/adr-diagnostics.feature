Feature: Diagnostics registry, renderings and exit codes

  @EndToEnd
  Scenario: The seeded registry is valid
    Given the registry "spec/diagnostics.yaml"
    When I run "polypiler-tooling diagnostics validate"
    Then the exit code is 0
    And stdout ends with "0 problems"

  Scenario: A diagnostic with a source range renders as text
    Given an error PLY0013 with message "export 'Sum' not found in 'algo.cs'" at algo.cs 3:5-3:7
    When it is rendered as text
    Then the line is "algo.cs(3,5): error PLY0013: export 'Sum' not found in 'algo.cs'"

  Scenario: A diagnostic without a source range renders as JSON
    Given an error PLY0003 with message "unknown option --frm", rule "CLI.options", cause "the option is not defined for translate", workaround "use --from" and command "polypiler translate a.cs --frm csharp"
    When it is rendered as JSON
    Then the line is
      """
      {"code":"PLY0003","severity":"error","message":"unknown option --frm","source":null,"rule":"CLI.options","cause":"the option is not defined for translate","workaround":"use --from","requiredCapability":null,"command":"polypiler translate a.cs --frm csharp"}
      """

  Scenario Outline: The first error decides the exit code
    Given the diagnostics <codes> are reported in that order
    Then the bag's exit code is <exit>

    Examples:
      | codes                     | exit |
      | PLY0009                   | 0    |
      | PLY0009, PLY0030, PLY0051 | 6    |
      | PLY0051, PLY0030          | 5    |
      | PLY0030, PLY0001          | 70   |

  @EndToEnd
  Scenario: A duplicate code is refused
    Given the registry "tests/fixtures/infra/diagnostics/duplicate.yaml" listing PLY0030 twice
    When I run "polypiler-tooling diagnostics validate --registry tests/fixtures/infra/diagnostics/duplicate.yaml"
    Then the exit code is 1
    And stderr contains "error PLY0045: duplicate code PLY0030"

  @EndToEnd
  Scenario: A code outside its area's block is refused
    Given the registry "tests/fixtures/infra/diagnostics/wrong-block.yaml" registering PLY0150 for area "cli"
    When I run the validator on it
    Then the exit code is 1
    And stderr contains "PLY0150 is registered for area 'cli' but lies in the block of 'ir'"
