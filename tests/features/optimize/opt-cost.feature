@Layer.core
Feature: Per-target cost model

  A pass that has proven several forms equivalent asks one service which is cheapest on the selected
  target (#155). The costs come from the target profile's cost.yaml, never from constants in a pass;
  the objective of --opt weighs latency, size, memory and allocations, and a form the profile does not
  list is never offered. The two profiles are the test fixtures tests/fixtures/optimize/cost/wide.yaml
  and narrow.yaml, whose numbers are made up for these scenarios.

  Background:
    Given the profile "wide" with RotateLeft.u32 native latency 1 size 1, simd width 128 setup 4 perBlock 1, swar registerBits 64 setup 4 perBlock 1, threads count 4 spawn 10000
    And the profile "narrow" with RotateLeft.u32 sequence latency 3 size 3 and helper call latency 12 size 2 body size 20, swar registerBits 64 setup 4 perBlock 1, no simd and no threads
    And both profiles have scalar byte Xor latency 1 and loop overhead 2

  Scenario: A native instruction beats the sequence and the helper
    When the cost model chooses the form of RotateLeft.u32 under profile "wide" with objective Speed
    Then the decision is "native" with score 1

  Scenario Outline: Without a native instruction the objective and the number of call sites decide
    When the cost model chooses the form of RotateLeft.u32 at <sites> call sites under profile "narrow" with objective <objective>
    Then the decision is "<form>" with score <score>

    Examples:
      | sites | objective | form     | score |
      | 10    | Speed     | sequence | 30    |
      | 10    | Size      | sequence | 30    |
      | 30    | Size      | helper   | 80    |

  Scenario Outline: The data path follows the element count and the available hardware
    When the cost model chooses the data path of a byte Xor over <n> elements under profile "<profile>" with objective Speed
    Then the decision is "<path>" with score <score>

    Examples:
      | n  | profile | path     | score |
      | 64 | wide    | simd-128 | 8     |
      | 64 | narrow  | swar-64  | 12    |
      | 3  | wide    | scalar   | 3     |

  Scenario: Threads are chosen only when the work outweighs the spawn cost
    When the cost model chooses the placement of 1000000 work units under profile "wide" with objective Speed
    Then the decision is "threads-4" with score 260000
    When the cost model chooses the placement of 10000 work units under profile "wide" with objective Speed
    Then the decision is "cpu" with score 10000

  Scenario: A form the profile does not offer is never chosen
    When the cost model chooses the placement of 1000000 work units under profile "narrow" with objective Speed
    Then the decision is "cpu" with score 1000000
    And IsAvailable("threads") is false and IsAvailable("gpu") is false

  Scenario: A target without a cost profile is an error
    When the cost model is created for target profile "csharp/net20" and no cost.yaml exists for it
    Then error PLY0355 "target profile 'csharp/net20' has no cost profile" is reported

  Scenario: A negative cost is rejected
    Given a profile file whose RotateLeft.u32 native latency is -1
    When the profile is loaded
    Then error PLY0356 names the key "operations.RotateLeft.u32.native.latency"
