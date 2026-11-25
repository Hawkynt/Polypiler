@Layer.core
Feature: Nullable standard output text

  Scenario: A null string is written as one blank line
    Given a verified IR function that writes "first" and then a null string
    When the function runs with an output sink
    Then the sink receives "first" followed by the empty string
    And the function completes normally

  Scenario: Executing the same write without an output sink reports the configuration error
    Given a verified IR function that writes a null string
    When the function runs without an output sink
    Then it raises InvalidOperationException mentioning "output sink"
