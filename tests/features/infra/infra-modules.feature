@Layer.core
Feature: Module projects and dependency direction

  Scenario: The repository satisfies its own module rules
    Given the rule file "tooling/ci/module-rules.yaml"
    And every "*.csproj" under the repository root
    When the dependency rules are checked
    Then there are 0 violations
    And the solution "Polypiler.slnx" lists each of the 21 projects named in this issue

  Scenario Outline: A reference from a lower to a higher layer is refused
    Given a project "<from>" that references "<to>"
    When the dependency rules are checked
    Then there is 1 violation with rule "<rule>"
    And its message starts with "PLY0038: <from> must not reference <to>"

    Examples:
      | from                          | to                               | rule          |
      | Polypiler.Core.IR             | Polypiler.Core.Analysis          | layer 2 -> 3  |
      | Polypiler.Core.Types          | Polypiler.Core.IR                | layer 1 -> 2  |
      | Polypiler.Core.Backends.Rust  | Polypiler.Core.Frontends.CSharp  | restricted    |
      | Polypiler.Core.Recognize      | Polypiler.Core.Recovery          | same layer    |
      | Polypiler.Core.Optimize       | Polypiler.Base                   | standalone    |

  Scenario: The documented same-layer exception is allowed
    Given a project "Polypiler.Core.Recovery" that references "Polypiler.Core.Recognize"
    When the dependency rules are checked
    Then there are 0 violations

  Scenario: A project the rules do not know is refused
    Given a project "Polypiler.Experimental" that references "Polypiler.Core"
    When the dependency rules are checked
    Then there is 1 violation
    And its message is "PLY0038: project Polypiler.Experimental is not covered by tooling/ci/module-rules.yaml"

  Scenario: A reference cycle is reported once
    Given a project "Polypiler.Core.Optimize" that references "Polypiler.Core.Legalize"
    And a project "Polypiler.Core.Legalize" that references "Polypiler.Core.Optimize"
    When the dependency rules are checked
    Then exactly 1 violation has rule "cycle"
