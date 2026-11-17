@EndToEnd @Layer.core
Feature: polypiler translate

  The exit codes are those of the diagnostic registry (spec/diagnostics.yaml): PLY0013 is an
  invalid source (4), PLY0014 and an unsupported construct are 3.

  Background:
    Given the fixtures of "tests/fixtures/cli/translate"

  Scenario: A C# function is translated to JavaScript and verified
    When I run "polypiler translate tests/fixtures/cli/translate/sum32.cs --from csharp --to javascript --export Add --verify --vectors tests/fixtures/cli/translate/sum32.vectors.json --output out/sum32-js"
    Then the exit code is 0
    And the last line of stdout is "out/sum32-js: verified (3 of 3 vectors)"
    And "out/sum32-js/polypiler.manifest.json" has "state" equal to "verified"
    And running "out/sum32-js/Sum32.mjs" under node gives Add(4294967295, 1) = 0

  Scenario: Without --verify the result is only generated
    When I run "polypiler translate tests/fixtures/cli/translate/sum32.cs --from csharp --to javascript --output out/sum32-gen"
    Then the exit code is 0
    And "out/sum32-gen/polypiler.manifest.json" has "state" equal to "generated"

  Scenario: Equivalent invocations record one canonical command line
    When I run "polypiler translate tests/fixtures/cli/translate/sum32.cs --from csharp --to javascript --export Add --output out/canon-a"
    And I run "polypiler translate --quiet --to=javascript --export Add --export Add --opt default --output out/canon-b --from csharp tests/fixtures/cli/translate/sum32.cs"
    Then every exit code is 0
    And "requestedConfig" of "out/canon-a/polypiler.manifest.json" equals that of "out/canon-b/polypiler.manifest.json"
    And it starts with "translate tests/fixtures/cli/translate/sum32.cs --export Add --from csharp --source-profile net10.0 --target-profile es2022 --to javascript"
    And it ends with " --to javascript --opt default"
    And it contains none of "--output", "--quiet"

  Scenario: An unsupported construct fails and leaves nothing behind
    When I run "polypiler translate tests/fixtures/cli/translate/money.cs --from csharp --to javascript --verify --output out/money-js"
    Then the exit code is 3
    And stderr contains "money.cs(1,"
    And stderr contains "error PLY1002"
    And the directory "out/money-js" does not exist

  Scenario: A semantic mismatch is never labelled verified
    When I run "polypiler translate tests/fixtures/cli/translate/sum32.cs --from csharp --to javascript --verify --vectors tests/fixtures/cli/translate/sum32.wrong.vectors.json --output out/sum32-bad"
    Then the exit code is 5
    And stderr contains "error PLY0051"
    And "out/sum32-bad/polypiler.manifest.json" has "state" equal to "executed"
    And "out/sum32-bad/polypiler.manifest.json" has "verification.result" equal to "failed"

  Scenario: A known language without a backend is reported as unsupported
    When I run "polypiler translate tests/fixtures/cli/translate/sum32.cs --from csharp --to ruby --output out/sum32-rb"
    Then the exit code is 3
    And stderr contains "error PLY0014: no backend for 'ruby' with profile"
    And the directory "out/sum32-rb" does not exist

  Scenario: An unknown language identifier is a usage error
    When I run "polypiler translate tests/fixtures/cli/translate/sum32.cs --from csharp --to klingon --output out/x"
    Then the exit code is 2
    And stderr contains "error PLY0006: unknown language 'klingon'"

  Scenario: A missing export is reported
    When I run "polypiler translate tests/fixtures/cli/translate/sum32.cs --from csharp --to javascript --export Mul --output out/x"
    Then the exit code is 4
    And stderr contains "error PLY0013: export 'Mul' not found in 'tests/fixtures/cli/translate/sum32.cs'"
