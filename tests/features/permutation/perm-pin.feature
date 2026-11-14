@EndToEnd
Feature: BitPerm fixtures exported from the pinned page

  Scenario: The export reproduces the checked-in fixtures
    Given the pinned BitPerm page from the corpus
    When I run the exporter as "node tooling/bitperm/export.mjs --page <page> --out tests/fixtures/bitperm --check"
    Then the exit code is 0
    And "tests/fixtures/bitperm/blocks.json" lists 16 blocks and "presets.json" lists 25 presets
    And "tests/fixtures/bitperm/tests.json" lists 51 tests in 10 sections

  Scenario Outline: Preset values equal the page's table
    Given the exported fixture "tests/fixtures/bitperm/presets.json"
    Then preset "<id>" has cost "<key>" equal to <value>

    Examples:
      | id     | key    | value |
      | modern | bmi    | 3     |
      | zen1_2 | bmi    | 18    |
      | 6502   | shift  | 2     |
      | 6502   | mul    | 999   |
      | i8086  | mul    | 118   |

  Scenario: Raw vectors of the reverse pattern match a hand computation
    Given the exported fixture "tests/fixtures/bitperm/vectors/pattern-reverse-8.json"
    Then it holds 256 vectors
    And input "0xB4" has output "0x2D" with mask "0xFF"

  Scenario: A changed page is refused, not silently re-exported
    Given a copy of the page in which the line "// --- VISUALIZER ---" is deleted
    When I run the exporter on it
    Then the exit code is 1
    And stderr contains "bitperm export: cut marker '// --- VISUALIZER ---' not found"

  Scenario: Drift between page and fixtures fails the check
    Given a copy of "presets.json" in which the cost "mul" of "modern" is 4
    When I run the exporter with --check against that copy
    Then the exit code is 1
    And stderr contains "presets.json differs from the pinned page"

  Scenario: A page with another number of CPU declarations is refused
    Given a copy of the page with the line "const CPU_X = 'x';" after the line "const CPU_CUSTOM = 'custom';"
    When I run the exporter on it
    Then the exit code is 1
    And stderr contains "bitperm export: expected 25 CPU declarations, found 26"

  Scenario: A page that cannot be read is refused
    When I run the exporter as "node tooling/bitperm/export.mjs --page missing.html --out out"
    Then the exit code is 1
    And stderr contains "bitperm export: cannot read missing.html"

  Scenario: An expected count the page does not have is refused
    Given the pinned BitPerm page from the corpus
    When I run the exporter as "node tooling/bitperm/export.mjs --page <page> --out out --check --expect-blocks 17"
    Then the exit code is 1
    And stderr contains "bitperm export: expected 17 blocks, found 16"

  Scenario: A fixture file that is missing fails the check
    Given a copy of the fixtures without "vectors/pattern-clear-8.json"
    When I run the exporter with --check against that copy
    Then the exit code is 1
    And stderr contains "vectors/pattern-clear-8.json is missing"

  Scenario: The raw evaluation reads every kind of mapping entry
    When the exporter evaluates the 5-bit mapping "src 1, src 1 inverted, const 0, const 1, empty" on input "0b10"
    Then the output is "0b01001" and the mask is "0b01111"
