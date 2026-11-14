Feature: Error catalogue generated from the diagnostics registry

  @EndToEnd
  Scenario: Index and pages are generated for every registered code
    Given the registry fixture with the codes PLY0004, PLY0009, PLY0031 and the retired PLY0035
    When I run "report errors --registry registry.yaml --notes notes-valid --out out"
    Then the exit code is 0
    And the directory "out" contains exactly "README.md", "PLY0004.md", "PLY0009.md", "PLY0031.md" and "PLY0035.md"
    And "out/README.md" contains the row "| [PLY0004](PLY0004.md) | error | 1 (EnvironmentError) | `cannot read input '{0}'` | CLI-FOUNDATION |"
    And "out/README.md" contains the row fragment "| [PLY0031](PLY0031.md) | error | 10 (BudgetExceeded) |"

  Scenario: A strict-promoted warning and a retired code are marked
    Given the same registry fixture
    When the catalogue is generated
    Then the index row of PLY0009 shows the exit code "— (3 with --strict)"
    And the page "PLY0035.md" has the status "retired"

  Scenario: Notes are merged and missing notes are stated
    Given a notes file "PLY0031.md" with the section "## What to do" containing "Raise the limit in tooling/ci/runner.yaml or shorten the input."
    When the catalogue is generated
    Then "PLY0031.md" contains that sentence under "## What to do"
    And "PLY0031.md" contains "No extended explanation has been written for this code yet." under "## Cause"

  @EndToEnd
  Scenario: A stale catalogue fails the check
    Given a generated catalogue in "out"
    And the registry message of PLY0004 is changed to "cannot open input '{0}'"
    When I run "report errors --registry registry.yaml --notes notes-valid --out out --check"
    Then the exit code is 1
    And stderr contains "PLY0091: generated document out of date: 'out/PLY0004.md' differs"
    And stderr contains "PLY0091: generated document out of date: 'out/README.md' differs"
    And no file in "out" was modified

  @EndToEnd
  Scenario: A note for an unregistered code is refused
    Given the notes folder also contains "PLY0777.md"
    When I run "report errors --registry registry.yaml --notes notes --out out"
    Then the exit code is 1
    And stderr contains "PLY0091: note 'notes/PLY0777.md' belongs to no registered code"
    And the directory "out" contains no file
