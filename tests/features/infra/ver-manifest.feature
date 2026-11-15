Feature: Generated manifest schema, writer and validator

  Scenario Outline: The state is derived from the recorded evidence
    Given a manifest with build exit code <build>, execution exit code <run> and verification <passed> of <total> passed
    When the manifest is written
    Then its "state" is "<state>"

    Examples:
      | build | run  | passed | total | state     |
      | none  | none | none   | none  | generated |
      | 0     | none | none   | none  | built     |
      | 0     | 0    | none   | none  | executed  |
      | 0     | 0    | 3      | 3     | verified  |
      | 0     | 0    | 2      | 3     | executed  |
      | 1     | none | none   | none  | generated |
      | 0     | 0    | 0      | 0     | executed  |

  Scenario: Inputs are recorded with their hash and provenance
    Given an input file with the 3-byte content "abc" taken from "Hawkynt/Hawkynt.github.io" at commit "b3a8a8e92cbca83475986391c3cac56f0b185d29"
    When the manifest is written
    Then "inputs[0].sha256" is "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
    And "inputs[0].provenance.commit" is "b3a8a8e92cbca83475986391c3cac56f0b185d29"

  Scenario: Writing the same manifest twice gives identical bytes
    Given the manifest of fixture "verified.json" with its "outputs" list in reverse order
    When it is serialized twice
    Then both serializations are byte-identical
    And "outputs" is sorted by "path"
    And the text contains no absolute path and no timestamp

  Scenario: A manifest that claims verified without results is rejected
    Given the file "tests/fixtures/infra/manifest/verified-without-results.json"
    When it is validated
    Then diagnostic PLY0054 is reported with "claims 'verified' but the evidence supports only 'executed'"
    And the mapped exit code is 1

  Scenario: A manifest with an unknown state fails the schema
    Given the file "tests/fixtures/infra/manifest/unknown-state.json" whose "state" is "certified"
    When it is validated
    Then diagnostic PLY0053 is reported at JSON pointer "/state"

  Scenario: translate writes a manifest beside the files it generates
    Given the file "Snippet.cs" with content "public static class Snippet { public static double F(double x) => System.Math.Sin(x) * 2; }"
    When I run "polypiler translate Snippet.cs --from csharp --to javascript --output out"
    Then the exit code is 0
    And the manifest "out/polypiler.manifest.json" is valid with state "generated"
    And the manifest "out/polypiler.manifest.json" records "out/Snippet.mjs" with its SHA-256 and "Snippet.cs" as "../Snippet.cs"
    And the manifest "out/polypiler.manifest.json" lists the native functions the header of "out/Snippet.mjs" lists
