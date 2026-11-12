Feature: One canonical issue-readiness standard
  Scenario: Existing templates lead to one policy
    Given ISSUES.md and the nine kind-specific issue templates
    When their guidance and navigation are checked
    Then they use the lifecycle and 95 percent line and branch coverage requirements from ISSUES.md
    And no second normative readiness policy or generic implementation template is introduced

  Scenario: The documentation example is complete before implementation
    Given the hypothetical Ready example in docs/examples/ISSUE-READINESS.md
    When it is checked against every required issue section
    Then its scope, evidence, scenarios, tests, measurements and ownership are specified
    And readiness does not depend on an implementation already existing or passing

  Scenario: Missing ABI facts cannot be marked irrelevant
    Given the Blocked example has no pinned DLL and an unknown PowerBASIC dialect, bitness and calling convention
    When its readiness rationale is checked
    Then it identifies those facts and their resolution owners as blockers
    And it does not replace the unknown facts with N/A or claim verified support

  Scenario: Changed requirements revoke readiness
    Given a Ready work order changes its pinned source hash or target calling convention
    When the documented lifecycle is followed
    Then affected readiness evidence must be reviewed again before implementation continues
    And ambiguity returns it to Draft or an unavailable prerequisite puts it in Blocked

  Scenario: Broken documentation navigation is detected
    Given a documentation-check fixture whose relative link targets missing-readiness-example.md
    When the documentation link check runs
    Then it reports that source link and missing target as a failure
    And the committed navigation links resolve to tracked files
