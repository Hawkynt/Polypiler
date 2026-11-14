Feature: Evidence bundle per BDD scenario

  @translation
  Scenario: A translation scenario leaves a complete bundle
    Given the source file "tests/fixtures/verify/evidence/source.txt" with content "abc"
    And the empty target file "tests/fixtures/verify/evidence/target.txt"
    And the manifest "tests/fixtures/verify/evidence/sample.manifest.json"
    And tool "node" at the version resolved from the toolchain lock
    And a process "run" with stdout "024d0127", empty stderr and exit code 0
    And a bit-exact comparison of 1 vector with reference "024d0127" and target "024d0127"
    When the scenario ends
    Then the bundle directory is "artifacts/evidence/evidence-bundle-per-bdd-scenario/a-translation-scenario-leaves-a-complete-bundle"
    And "evidence.json" records source hash "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
    And it records target code hash "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
    And it records exactness "bit-exact", an empty "diff.txt" and the ciLink "local" outside CI
    And the bundle validates without diagnostics

  Scenario Outline: Titles become stable folder names
    Given the title "<title>"
    When its slug is computed
    Then the slug is "<slug>"

    Examples:
      | title                          | slug                         |
      | JavaScript Cipher to C#        | javascript-cipher-to-c       |
      | A→B→A round trip (u32)         | a-b-a-round-trip-u32         |
      | --opt none vs --opt speed      | opt-none-vs-opt-speed        |
      | §                              | scenario                     |

  Scenario: A translation bundle without a comparison is incomplete
    Given a bundle for a scenario tagged "@translation" with source, target code, manifest, tool version and process
    But no comparison
    When the bundle is validated
    Then diagnostic PLY0060 is reported with "missing reference result, target result, diff, exactness policy"
    And the mapped exit code is 1

  Scenario: A passed scenario with a failed vector contradicts itself
    Given a bundle with outcome "passed" for a scenario tagged "@translation"
    And a comparison with reference "b90f2ae4" and target "aece2ad5"
    When the bundle is validated
    Then diagnostic PLY0060 is reported with "contradicts its outcome: 1 vector(s) failed"
    And "diff.txt" contains "expected b90f2ae4 actual aece2ad5 @0"

  Scenario: An infrastructure scenario needs only identity, tools and the CI link
    Given a scenario without the "@translation" tag that recorded tool "dotnet-sdk"
    When the scenario ends
    Then the bundle validates without diagnostics
    And "evidence.json" has "manifest" null and "reference" null
