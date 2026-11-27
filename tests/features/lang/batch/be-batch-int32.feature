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

  Scenario: Dictionary keys retain their ordinal identity and aliases
    Given the pinned C# map alias fixture
    When it is translated under the cmd profile
    Then the native and translated dictionaries both return 42 without output

  Scenario: A missing dictionary key produces its trap marker
    Given a C# dictionary kernel reading a missing key
    When it is translated under the cmd profile
    Then the native Batch script reports KeyNotFound

  Scenario: A map with a wider value type is refused
    Given a C# kernel allocating an Int64 dictionary
    When it is translated under the cmd profile
    Then it reports PLY2850 and writes no output file
