@Layer.core
Feature: Option and identifier contributions

  An area adds options to a command it does not own, and identifiers to --from and --to, by
  registering one contributor. The scenarios register test-only contributors on the test-only
  command "probe", which has its own "--opt <level>" with the default "balanced", "--from" and
  "--to", beside "echo" and "translate", and run them in process.

  Scenario: A contributed option is parsed, shown in help and rendered canonically
    Given a contributor owned by "DEMO-ISSUE" that adds "--demo-level <n>" and "--demo-flag" to the command "probe"
    When I run "polypiler probe input.txt --demo-flag --demo-level 3 --opt speed"
    Then the exit code is 0
    And the option bag holds demo-level 3 and demo-flag true
    And the canonical arguments are "--opt speed --demo-flag --demo-level 3"
    And "polypiler probe --help" contains the heading "Options from DEMO-ISSUE:" followed by "--demo-flag" and "--demo-level"

  Scenario: Contributed options are recorded in their normal spelling, ordered by name
    Given a contributor owned by "DEMO-ISSUE" that adds to "probe" the set-list "--demo-tags", the single-valued "--demo-address" whose normal spelling is "0x" followed by four upper-case hexadecimal digits, and the flag "--demo-trace" declared not recorded
    When I run "polypiler probe input.txt --demo-trace --demo-tags b,a --demo-address 255 --demo-tags a"
    Then the exit code is 0
    And the canonical arguments are "--opt balanced --demo-address 0x00FF --demo-tags a,b"

  Scenario: An option nobody registered for the command is refused
    Given the same contributor, registered for "probe" only
    When I run "polypiler translate input.cs --from csharp --to javascript --demo-level 3 --output out"
    Then the exit code is 2
    And stderr contains "error PLY0003: unknown option '--demo-level'"

  Scenario: Two owners cannot register the same option for one command
    Given a contributor owned by "DEMO-ISSUE" that adds "--demo-level" to "probe"
    When a contributor owned by "OTHER-ISSUE" that adds "--demo-level" to "probe" is registered
    Then registration throws InvalidOperationException with message "option '--demo-level' of command 'probe' is already registered by DEMO-ISSUE (second registration: OTHER-ISSUE)"

  Scenario: A contributed identifier is accepted for the direction it was registered for
    Given an identifier contributor that registers "demo-format" for direction "From"
    When I run "polypiler probe input.bin --from demo-format"
    Then the exit code is 0
    And "polypiler probe input.bin --to demo-format" exits with 2 and "error PLY0006: unknown language 'demo-format'"

  Scenario: A contributor's own diagnostic stops the command
    Given the contributor reports error "PLY9001" with exit code 2 for "--demo-level 0"
    When I run "polypiler probe input.txt --demo-level 0"
    Then the exit code is 2
    And the command's Execute method is not called
