@Layer.core @EndToEnd
Feature: Lift Windows Batch integer kernels
  Scenario: The countdown returns 42
    Given the pinned Batch countdown fixture
    When it is translated under the cmd profile
    Then the native and translated results are both 42

  Scenario: A construct outside the profile is refused
    Given a Batch kernel containing "call %target%"
    When it is translated under the cmd profile
    Then it reports PLY2802 and writes no output file

  Scenario: Compound FOR separates delayed reads from block-entry reads
    Given the pinned Batch delayed expansion fixture
    When it is translated under the cmd profile
    Then the native and translated results are both 140 with no native output

  Scenario: A zero-trip loop does not read a delayed variable
    Given the pinned Batch delayed expansion zero-trip fixture
    When it is translated under the cmd profile
    Then the native and translated results are both 7 with no native output

  Scenario: An executing loop cannot read an unbound delayed variable
    Given a Batch loop with an unbound delayed variable
    When it is translated under the cmd profile
    Then it reports PLY2802 and writes no output file
