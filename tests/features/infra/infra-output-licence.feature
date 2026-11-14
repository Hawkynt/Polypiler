@EndToEnd
Feature: Emitted helper code is MIT

  Scenario: A translated MIT program stays MIT
    Given a C# source whose licence is "MIT"
    When it is translated to JavaScript with helpers emitted
    Then the manifest lists every emitted helper with its origin and licence
    And the effective licence of the output is "MIT"

  Scenario: LGPL-2.1-only sources are allowed
    Given a corpus file whose SPDX header is "LGPL-2.1-only"
    When "polypiler-tooling licenses scan --mode corpus" runs on its tree
    Then the exit code is 0
    And it reports no violation for that file

  Scenario: The GPL fence still holds
    Given a file under "Polypiler.Core.Backends.CSharp" with an SPDX header "GPL-3.0-or-later"
    When "polypiler-tooling licenses scan --mode project" runs on its tree
    Then the exit code is 8
    And stderr contains "PLY0024"
