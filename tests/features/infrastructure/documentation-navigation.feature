@Layer.core
Feature: Navigate the compiler design and requirements
  Scenario: Find every PRD chapter through the index
    Given docs/PRD.md and its ten linked parts
    When the structure is checked
    Then chapters 1 through 34 appear exactly once across the parts
    And original subsection numbers and requirement text are preserved

  Scenario: Follow old and new requirement links
    Given the original PRD heading anchors and the new part navigation
    When documentation links are checked
    Then each legacy anchor points to its moved section
    And index, previous, next and architecture links resolve

  Scenario: Read rendered architecture diagrams
    Given README.md and all seven architecture guides
    When their Mermaid blocks are rendered
    Then each document has a valid diagram matching its described design
    And the proof, capability and oracle boundaries remain explicit

  Scenario: Reject broken documentation structure
    Given a test fixture with a missing target or duplicate PRD chapter
    When the documentation checks run
    Then the invalid fixture is rejected with the offending target or chapter
