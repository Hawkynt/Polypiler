@Layer.core @EndToEnd
Feature: Emit Windows Batch integer kernels
  Scenario: The countdown returns 42
    Given the pinned C# countdown fixture
    When it is translated under the cmd profile
    Then the native and translated results are both 42

  Scenario: A construct outside the profile is refused
    Given a C# kernel returning an Int64 value
    When it is translated under the cmd profile
    Then it reports PLY2850 and writes no output file
