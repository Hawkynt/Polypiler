@EndToEnd
Feature: Licence scanner and policy gate

  Scenario: A corpus file without a header inherits the nearest licence file
    Given the imported corpus fixture "cipher.checksum.adler32", declared "LGPL-3.0-or-later" in corpus.yaml
    And corpus.yaml declares "Hawkynt/Hawkynt.github.io" as "LGPL-3.0-or-later" with licence files "LICENSE" and "Cipher/LICENSE"
    When I run "licenses scan --mode corpus"
    Then the exit code is 0
    And the report entry for "Cipher/algorithms/checksum/adler.js" has licence "LGPL-3.0-or-later", source "licence-file" and status "allowed"

  Scenario: A nested licence file overrides the root, and a GPL source is allowed
    Given the tree "gpl-nested" with an LGPL-3.0 "LICENSE" at the root and a GPL-3.0 "LICENSE" in "sub", declared "GPL-3.0-only" for "sub/**"
    When I run "licenses scan --mode distribution --root gpl-nested"
    Then the exit code is 0
    And the report entry for "sub/x.c" has licence "GPL-3.0-only", source "licence-file" and status "allowed"
    And the report entry for "other/z.c" has licence "LGPL-3.0-or-later" and status "allowed"

  Scenario: An SPDX header takes precedence over the licence file
    Given the tree "mit-header" whose file "a.js" starts with "// SPDX-License-Identifier: MIT" beside an LGPL-3.0 LICENSE file
    When I run "licenses scan --mode distribution --root mit-header"
    Then the exit code is 0
    And the report entry for "a.js" has licence "MIT" and source "header"

  Scenario Outline: A reference-only licence is an oracle in the corpus and a blocker in a distribution
    Given the tree "agpl-header" whose file "ref.c" starts with "// SPDX-License-Identifier: AGPL-3.0-only"
    When I run "licenses scan --mode <mode> --root agpl-header"
    Then the exit code is <exit>
    And the report entry for "ref.c" has status "<status>"

    Examples:
      | mode         | exit | status         |
      | corpus       | 0    | reference-only |
      | distribution | 8    | denied         |

  Scenario Outline: SPDX expressions are evaluated, not string-matched
    Given a file whose header declares "<expression>"
    When it is scanned in distribution mode
    Then its status is "<status>"

    Examples:
      | expression                    | status         |
      | MIT AND GPL-3.0-only          | allowed        |
      | MIT OR AGPL-3.0-only          | allowed        |
      | MIT AND AGPL-3.0-only         | denied         |
      | (MIT OR Apache-2.0) AND ISC   | allowed        |
      | MIT OR AGPL-3.0-only AND ISC  | allowed        |
      | GPL-2.0-only                  | denied         |
      | Proprietary-Foo               | denied         |

  Scenario: A file with no licence anywhere is refused
    Given the tree "no-licence" containing only "orphan.py" without header and without a licence file
    When I run "licenses scan --mode corpus --root no-licence"
    Then the exit code is 8
    And stderr contains "PLY0029: 'orphan.py': no licence could be determined"

  Scenario: Model weights are blocked from redistribution by default
    Given the tree "weights" containing "model.bin" beside an MIT LICENSE file
    When I run "licenses scan --mode distribution --root weights"
    Then the exit code is 8
    And stderr contains "PLY0024: 'model.bin' is a restricted asset and may not be redistributed"
