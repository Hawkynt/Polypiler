@EndToEnd
Feature: File-level licence audit of the NCNN adapter's native inputs

  Background:
    Given the corpus holds Hawkynt/MediaDroid at 7f8b6b995cbe54a6afb290a900748393bf01e272

  Scenario: The committed audit validates against the pinned corpus
    When "polypiler-tooling catalog audit-adapter --audit spec/adapters/ncnn/licence-audit.yaml" runs
    Then the exit code is 0
    And the record "app/src/main/assets/models/rife-4.25-lite/flownet.param" has bytes 35830 and sha256 "e55b76f197b89637dc824ce89e64878df419214d3cf6a10aea0283c61b93a908"

  Scenario: A usable file may be touched
    When the validator runs with --touch native/src/warp_layer.cpp
    Then the exit code is 0

  Scenario: XVFI may not be touched
    When the validator runs with --touch native/src/xvfi.cpp
    Then the exit code is 8
    And the diagnostic is PLY0950 "'native/src/xvfi.cpp' is blocked in the licence audit" naming "research and education only"

  Scenario: A file without a record may not be touched
    When the validator runs with --touch native/jni/desktop.cpp
    Then the exit code is 8
    And the diagnostic is PLY0950 "'native/jni/desktop.cpp' has no licence audit record"

  Scenario: A record that no longer matches the pinned bytes is stale
    Given the fixture audit "stale.yaml" records sha256 "0000000000000000000000000000000000000000000000000000000000000000" for "native/src/networks.h"
    When the validator runs on it
    Then the exit code is 8
    And the diagnostic is PLY0950 containing "is stale"

  Scenario: A refused clearance blocks every touch
    Given the fixture audit "refused.yaml" has clearance status "refused"
    When the validator runs with --touch native/src/warp_layer.cpp
    Then the exit code is 8
    And the diagnostic is PLY0950 containing "licence clearance was refused"

  Scenario Outline: A defective audit is refused with every violation named
    Given the fixture audit "<fixture>"
    When the validator runs on it
    Then the exit code is <exit>
    And the diagnostic is <code> containing "<detail>"

    Examples:
      | fixture                         | exit | code    | detail                                                                         |
      | malformed-sha.yaml              | 8    | PLY0950 | /records/1/sha256                                                              |
      | zero-bytes.yaml                 | 8    | PLY0950 | (6848 bytes, recorded 0 bytes)                                                 |
      | blocked-without-obligation.yaml | 8    | PLY0950 | is blocked without a no-redistribution or research-only obligation            |
      | missing-reason.yaml             | 8    | PLY0950 | record 'native/src/networks.cpp' is reference-only without a reason            |
      | unknown-notice.yaml             | 1    | PLY0025 | 'app/THIRD-PARTY-NOTICE.NOPE.txt' does not exist in Hawkynt/MediaDroid         |
      | contradiction.yaml              | 8    | PLY0950 | record 'native/src/networks.cpp' says 'MIT', the licence scanner detects       |
      | not-yaml.yaml                   | 8    | PLY0950 | licence audit: not valid YAML                                                  |
      | duplicate.yaml                  | 8    | PLY0950 | two records for 'native/src/networks.h'                                        |

  Scenario: An audit without records is valid, and nothing may be touched under it
    Given the fixture audit "empty.yaml"
    When the validator runs on it
    Then the exit code is 0
    When the validator runs with --touch native/src/networks.cpp
    Then the exit code is 8
    And the diagnostic is PLY0950 "'native/src/networks.cpp' has no licence audit record"
