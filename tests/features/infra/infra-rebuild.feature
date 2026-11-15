@EndToEnd
Feature: Deterministic rebuild check

  # The generator is copied into every run's fresh working directory with "--input generator"; the
  # toolchain key of "dotnet" in tooling/toolchains.lock is "dotnet-sdk".

  Scenario: A deterministic generator passes and its tree hash is stable
    Given the generator verb "stable" writing "a.txt" with content "hello\n" and an empty "b/c.txt"
    When I run "rebuild check --output out --input generator -- dotnet-sdk generator/polypiler-generator.dll stable {output}"
    Then the exit code is 0
    And stdout contains "7be606a71e0da97c2698969020a6b713ef3d0d64927ea1bb4ab4c3e0c4885443"

  Scenario: A timestamp in the output is detected
    Given the generator verb "timestamp"
    When I run "rebuild check --output out --input generator -- dotnet-sdk generator/polypiler-generator.dll timestamp {output}"
    Then the exit code is 1
    And stderr contains "PLY0036: rebuild not deterministic: 'stamp.txt' differs at byte"
    And stderr does not mention "a.txt"

  Scenario: Unordered iteration is detected
    Given the generator verb "unordered"
    When I run "rebuild check --output out --runs 3 --input generator -- dotnet-sdk generator/polypiler-generator.dll unordered {output}"
    Then the exit code is 1
    And stderr contains "PLY0036: rebuild not deterministic: 'keys.txt' differs at byte"

  Scenario: A file present in only one run is detected
    Given the generator verb "extra"
    When I run "rebuild check --output out --input generator -- dotnet-sdk generator/polypiler-generator.dll extra {output}"
    Then the exit code is 1
    And stderr contains "exists only in run 1"
    And stderr contains "exists only in run 2"

  Scenario: A documented run-dependent manifest field is ignored, an undocumented one is not
    Given rebuild-ignore.yaml lists the manifest pointer "/verification/durationMs" with reason "wall-clock time"
    When the rebuild check runs the generator verb "manifest-volatile"
    Then the exit code is 0
    When the rebuild check runs the generator verb "manifest-drift"
    Then the exit code is 1
    And stderr contains "PLY0036: rebuild not deterministic: 'polypiler.manifest.json' differs"

  Scenario: An ignore entry without a reason is refused
    Given rebuild-ignore.yaml lists the file glob "*.log" without a reason
    When I run "rebuild check --output out --input generator -- dotnet-sdk generator/polypiler-generator.dll stable {output}"
    Then the exit code is 1
    And stderr contains "PLY0026"

  Scenario: An empty output tree passes with the hash of the empty listing
    Given the generator verb "empty"
    When I run "rebuild check --output out --input generator -- dotnet-sdk generator/polypiler-generator.dll empty {output}"
    Then the exit code is 0
    And stdout contains "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"

  Scenario Outline: The number of runs is at least two
    Given the generator verb "stable"
    When I run "rebuild check --output out --runs <runs> --input generator -- dotnet-sdk generator/polypiler-generator.dll stable {output}"
    Then the exit code is <exit>

    Examples:
      | runs | exit |
      | 2    | 0    |
      | 1    | 2    |
      | 0    | 2    |

  Scenario: A run over the time limit is a runner diagnostic and nothing is compared
    Given the generator verb "sleep"
    When I run "rebuild check --output out --wall-time 1 --input generator -- dotnet-sdk generator/polypiler-generator.dll sleep {output}"
    Then the exit code is 10
    And stderr contains "PLY0031"
    And stderr does not mention "PLY0036"

  Scenario: The floating-point implementations the executable embeds are written identically
    Given the polypiler-tooling build output as "tooling"
    When I run "rebuild check --output out --input tooling -- dotnet-sdk tooling/polypiler-tooling.dll float-implementations {output}/float-implementations.irm"
    Then the exit code is 0
    And stdout contains "rebuild deterministic over 2 runs"
