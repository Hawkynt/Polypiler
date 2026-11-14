@EndToEnd
Feature: Fuzzing harness with corpus mutation, budgets and minimization

  Scenario: A crashing corpus entry is reported and minimized to its cause
    Given the fuzz target "demo-marker" with the corpus entry "seed.bin" containing the bytes 78 78 50 4C 59 78 78
    When the in-process engine runs 1 iteration with seed 1
    Then one finding of kind Crash is reported at iteration 0 with diagnostic PLY0070
    And the minimized input is the 3 bytes 50 4C 59
    And the finding record is "artifacts/fuzz/demo-marker/b8d17c5a6e84d640.json"
    And the record's "corpusEntrySha256" is the SHA-256 of "seed.bin"

  Scenario: Mutation finds an input the corpus does not contain
    Given the fuzz target "demo-zero" with the corpus entry "seed.bin" containing the byte 41
    When the in-process engine runs 10000 iterations with seed 1
    Then exactly one finding of kind Crash is reported
    And the minimized input is the single byte 00
    And a second run with seed 1 reports the same iteration number

  Scenario: An input that never returns is a hang, and the run continues
    Given the fuzz target "demo-hang" with the corpus entry "seed.bin" containing the byte FF
    When the in-process engine runs 3 iterations with seed 1 and a per-input budget of 1 second
    Then a finding of kind Hang is reported at iteration 0
    And iterations 1 and 2 were executed by a restarted worker

  Scenario: Runaway allocation is a finding
    Given the fuzz target "demo-alloc" with the corpus entry "seed.bin" containing the byte 01
    When the in-process engine runs 1 iteration with seed 1 and an allocation budget of 64 MiB
    Then a finding of kind Allocation is reported at iteration 0

  Scenario: A robust target yields no findings
    Given the fuzz target "cli-args" from the built CLI assembly with its corpus of 3 entries
    When the in-process engine runs 10000 iterations with seed 1
    Then no finding is reported and the tooling exit code is 0
    And the report counts 10000 executed iterations

  Scenario: An unknown target is a usage error
    When "fuzz run --target no-such-target" is invoked
    Then diagnostic PLY0003 is reported and the tooling exit code is 2
