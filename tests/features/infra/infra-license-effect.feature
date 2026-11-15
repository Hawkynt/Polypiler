@EndToEnd
Feature: Effective licence and the GPL fence

  Scenario Outline: The effective licence follows from the closure
    Given a closure with the licences <closure>
    When the effective licence is computed
    Then it is "<effective>"

    Examples:
      | closure                                    | effective            |
      | "MIT"                                      | MIT                  |
      | "MIT", "Apache-2.0"                        | Apache-2.0 AND MIT   |
      | "MIT", "LGPL-3.0-or-later"                 | LGPL-3.0-or-later    |
      | "LGPL-2.0-or-later", "LGPL-3.0-or-later"   | LGPL-3.0-or-later    |
      | "LGPL-3.0-or-later", "GPL-3.0-only"        | GPL-3.0-or-later     |
      | "MIT", "GPL-2.0-or-later"                  | GPL-3.0-or-later     |
      | "MIT OR GPL-3.0-only"                      | MIT                  |

  Scenario: A package translated from a GPL source is GPL and says so
    Given the package tree "pkg-gpl" whose manifest lists the input "gpl-nested/sub/x.c" (licence GPL-3.0-only by its nearest licence file) and states outputLicence "GPL-3.0-or-later"
    When I run "licenses scan --mode distribution --root pkg-gpl"
    Then the exit code is 0
    And "artifacts/licenses/effective.json" has effectiveLicence "GPL-3.0-or-later" and because ["gpl-nested/sub/x.c"]

  Scenario: A GPL-derived package labelled LGPL is refused
    Given the package tree "pkg-understated" with the same input and outputLicence "LGPL-3.0-or-later"
    When I run "licenses scan --mode distribution --root pkg-understated"
    Then the exit code is 8
    And stderr contains "PLY0024: 'pkg-understated': output licence 'LGPL-3.0-or-later' understates the effective licence 'GPL-3.0-or-later'"

  Scenario: An LGPL-derived package stays LGPL
    Given the package tree "pkg-lgpl" whose manifest lists the input "tests/ARITH.BAS" of "Hawkynt/PB-Compiler" and states outputLicence "LGPL-3.0-or-later"
    When I run "licenses scan --mode distribution --root pkg-lgpl"
    Then the exit code is 0
    And "artifacts/licenses/effective.json" has effectiveLicence "LGPL-3.0-or-later"

  Scenario: GPL-derived code cannot enter a compiler project
    Given the repository tree "proj-gpl-notice" whose folder "Polypiler.Demo" has a "THIRD_PARTY_NOTICES.md" entry with "Origin: https://example.org/gpl-lib:src/x.c" and "Licence: GPL-3.0-only"
    When I run "licenses scan --mode project --root proj-gpl-notice"
    Then the exit code is 8
    And stderr contains "PLY0024: 'Polypiler.Demo/THIRD_PARTY_NOTICES.md': GPL-derived code may not be merged into the LGPL-licensed compiler projects"

  Scenario: Permissive and LGPL origins pass the fence
    Given the repository tree "proj-clean" whose folder "Polypiler.Demo" has notice entries with licences "MIT" and "LGPL-2.1-or-later"
    When I run "licenses scan --mode project --root proj-clean"
    Then the exit code is 0

  Scenario: A reference-only source cannot be part of any output
    Given the package tree "pkg-reference-only" whose manifest lists an input under "GPL-2.0-only"
    When I run "licenses scan --mode distribution --root pkg-reference-only"
    Then the exit code is 8
    And stderr contains "is reference-only and cannot be part of an output"
