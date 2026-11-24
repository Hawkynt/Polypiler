@Layer.core @EndToEnd
Feature: Lift Windows Batch integer kernels
  Scenario: The countdown returns 42
    Given the pinned Batch countdown fixture
    When it is translated under the cmd profile
    Then the native and translated results are both 42

  Scenario: A construct outside the profile is refused
    Given a Batch kernel containing "call :unknown"
    When it is translated under the cmd profile
    Then it reports PLY2802 and writes no output file
