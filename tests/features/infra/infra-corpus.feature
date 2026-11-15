@EndToEnd @Layer.core
Feature: Corpus importer with locked manifest and fixture discovery

  Scenario: A pinned fixture is imported byte for byte
    Given corpus.yaml lists fixture "cipher.checksum.adler32" at "Hawkynt/Hawkynt.github.io" commit "b3a8a8e92cbca83475986391c3cac56f0b185d29"
    And a local checkout of that repository is passed with --source
    When I run "corpus import --fixture cipher.checksum.adler32" with that checkout as --source
    Then the exit code is 0
    And the file "corpus/upstream/Hawkynt/Hawkynt.github.io@b3a8a8e92cbca83475986391c3cac56f0b185d29/Cipher/algorithms/checksum/adler.js" has 11914 bytes
    And its SHA-256 is "4218b3ff959e32ad852855aeebe9f56f2319eb6c9dc495cc8526bcbde6797bbc"
    And "Cipher/OpCodes.js", "Cipher/LICENSE" and "LICENSE" of the same commit are present in the same checkout

  Scenario: Importing twice produces an identical lock
    Given the mini-upstream repository with files "a.txt", "dir/b.txt" and "LICENSE"
    When I run "corpus import --update-lock" twice
    Then both corpus.lock.json files are byte-identical
    And the lock lists "LICENSE", "a.txt", "dir/b.txt" in that order

  Scenario: A changed byte is detected
    Given an imported mini-upstream corpus whose lock records "a.txt" with content "hello\n"
    And "a.txt" in the workspace is replaced by "hellO\n"
    When I run "corpus verify"
    Then the exit code is 1
    And stderr contains "PLY0023"
    And stderr contains "the lock records 5891b5b522d5df086d0ff0b110fbd9d21bb4fc7163af34d08286a2e846f6be03"

  Scenario: A path that does not exist at the pin is refused
    Given corpus.yaml lists a fixture with path "Cipher/algorithms/checksum/adler33.js" at commit "b3a8a8e92cbca83475986391c3cac56f0b185d29"
    When I run "corpus import --update-lock"
    Then the exit code is 1
    And stderr contains "PLY0025"
    And corpus.lock.json is unchanged

  Scenario: A fixture naming an undeclared repository is refused
    Given corpus.yaml lists a fixture "x.y" with repository "Hawkynt/Unknown"
    When I run "corpus verify"
    Then the exit code is 1
    And stderr contains "PLY0026: corpus manifest invalid: fixture 'x.y' names undeclared repository 'Hawkynt/Unknown'"

  Scenario: Discovery lists candidates deterministically
    Given the repository "Hawkynt/Hawkynt.github.io" at its pin
    When I run "corpus discover --repository Hawkynt/Hawkynt.github.io --glob Cipher/algorithms/checksum/*.js"
    Then stdout lists 37 fixture stubs
    And the first path is "Cipher/algorithms/checksum/aba-routing.js" and the last is "Cipher/algorithms/checksum/xor-checksum.js"
    And corpus.yaml is unchanged
