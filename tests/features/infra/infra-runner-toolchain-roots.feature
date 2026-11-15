@OsIntegration
Feature: Toolchains installed under home run isolated

  Background:
    Given kotlinc 2.4.21 unpacked under a temporary home folder

  Scenario: kotlinc from a home-folder install runs inside bubblewrap
    When a Kotlin fixture is compiled and run through the runner with an untrusted plan
    Then it runs in the bubblewrap sandbox and prints the expected output
    And a file elsewhere in that home folder is not readable from the program

  Scenario: A toolchain cannot write its own install root
    When the program tries to create a file in its toolchain's root
    Then the attempt fails with a read-only error
