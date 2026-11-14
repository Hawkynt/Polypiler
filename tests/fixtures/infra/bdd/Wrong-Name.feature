Feature: A feature file named against the convention

  Scenario: The version is printed
    When I run "polypiler --version"
    Then the exit code is 0
