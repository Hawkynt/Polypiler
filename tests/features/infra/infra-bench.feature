@Performance
Feature: Benchmark harness for speed, memory and code size

  Scenario: A program is measured and its numbers are recorded, not judged
    Given the benchmark definition "tests/fixtures/bench/sum-loop.bench.yaml" with 1 warm-up and 5 runs on toolchain "node"
    When "bench run --id sum-loop" is invoked
    Then "artifacts/bench/sum-loop/default.json" contains exactly 5 samples
    And every sample has a positive "wallNs" and records "cpuNs" and "peakRssBytes"
    And each run printed "499999500000"
    And the summary row for "sum-loop" carries the note "record only"
    And the tooling exit code is 0

  Scenario Outline: Median and dispersion are computed without removing outliers
    Given the wall-time samples <samples> in milliseconds
    When the statistics are computed
    Then the median is <median> ms and the MAD is <mad> ms

    Examples:
      | samples            | median | mad |
      | 10, 12, 11, 50, 13 | 12     | 1   |
      | 10, 12, 14, 20     | 13     | 2   |
      | 7                  | 7      | 0   |

  Scenario: Size metrics are the byte sizes on disk
    Given a definition whose "sizes" lists "tests/fixtures/bench/size/abc.txt" as source
    When the benchmark is run
    Then "sourceBytes" is 3

  Scenario Outline: A budget set by an ADR gates a regression, equality passes
    Given a baseline result with median wall time 100 ms and a current result with median <current> ms on the same environment
    And the test budget file "tests/fixtures/bench/budgets-test.yaml" allowing 10 percent regression of "wallNs"
    When "bench compare" is invoked
    Then the outcome is <outcome>

    Examples:
      | current | outcome                      |
      | 90      | pass, change -10 %           |
      | 110     | pass, change +10 %           |
      | 111     | PLY0073, tooling exit code 1 |

  Scenario: Without a budget a slower result is recorded but does not fail
    Given a baseline result with median wall time 100 ms and a current result with median 500 ms on the same environment
    And no budget file
    When "bench compare" is invoked
    Then the table shows "+400 %" with the note "record only"
    And the tooling exit code is 0

  Scenario: Results from different environments are not compared
    Given a baseline result recorded with 8 logical cores and a current result recorded with 4 logical cores
    When "bench compare" is invoked
    Then diagnostic PLY0072 names the field "logicalCores" with "8" vs "4"
    And no relative change is printed for that benchmark

  Scenario: A run that prints the wrong result is not a measurement
    Given the benchmark definition "tests/fixtures/bench/sum-loop.bench.yaml" with "expectStdout" changed to "0"
    When "bench run --id sum-loop" is invoked
    Then no result file is written for "sum-loop"
    And diagnostic PLY0051 is reported and the tooling exit code is 5
