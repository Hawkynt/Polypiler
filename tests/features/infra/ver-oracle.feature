@Layer.core
Feature: Oracle and differential comparison framework

  @EndToEnd
  Scenario Outline: The reference implementation agrees with the published Adler-32 vectors
    Given the vector file "tests/fixtures/verify/oracle/adler32.vectors.json"
    And the source-toolchain oracle "node" running "tests/fixtures/verify/oracle/adler32.js"
    When the candidate "tests/fixtures/verify/oracle/adler32.js" is compared bit-exactly
    Then vector "<name>" passes with output <checksum>
    And the report counts 4 of 4 vectors passed

    Examples:
      | name      | checksum |
      | empty     | 00000001 |
      | a         | 00620062 |
      | abc       | 024d0127 |
      | 300 x ff  | b90f2ae4 |

  @EndToEnd
  Scenario: A candidate with the wrong modulus is reported with its first differing byte
    Given the vector file "tests/fixtures/verify/oracle/adler32.vectors.json"
    When the candidate "tests/fixtures/verify/oracle/adler32-wrong-modulus.js" is compared bit-exactly
    Then vector "abc" passes
    And vector "300 x ff" fails with expected b90f2ae4, actual aece2ad5 and first difference at byte 0
    And diagnostic PLY0051 is reported and the mapped exit code is 5

  Scenario Outline: Floating-point results are compared under the declared policy
    Given the reference f64 value with bits <reference> and the actual f64 value with bits <actual>
    When they are compared with policy <policy>
    Then the outcome is <outcome>

    Examples:
      | reference          | actual             | policy         | outcome                           |
      | 0x3FD3333333333333 | 0x3FD3333333333334 | exact          | PLY0051                           |
      | 0x3FD3333333333333 | 0x3FD3333333333334 | ulp 1          | pass, distance 1                  |
      | 0x3FD3333333333333 | 0x3FD3333333333335 | ulp 1          | PLY0052 distance 2                |
      | 0x0000000000000000 | 0x8000000000000000 | ulp 1          | PLY0052 special values differ     |
      | 0x0000000000000000 | 0x8000000000000000 | ulp 1, class   | pass, distance 0                  |
      | 0x0000000000000000 | 0x8000000000000000 | exact          | PLY0051                           |
      | 0x7FF8000000000000 | 0x7FF8000000000001 | ulp 1          | pass                              |
      | 0x7FF8000000000000 | 0x7FF8000000000001 | exact          | pass                              |
      | 0x7FF8000000000000 | 0x7FF8000000000001 | exact, bitwise | PLY0051                           |
      | 0x7FF0000000000000 | 0x7FEFFFFFFFFFFFFF | ulp 4          | PLY0052                           |

  Scenario: Polypiler's own output is refused as a reference
    Given a vector file inside a directory that contains "polypiler.manifest.json"
    When it is registered as the oracle for fixture "cipher.adler32"
    Then registration is rejected
    And verifying "cipher.adler32" reports PLY0063 with mapped exit code 7

  @EndToEnd
  Scenario: A reference that cannot run is an oracle failure, not a mismatch
    Given the source-toolchain oracle "node" running the missing file "tests/fixtures/verify/oracle/absent.js"
    When the candidate "tests/fixtures/verify/oracle/adler32.js" is compared bit-exactly
    Then diagnostic PLY0050 is reported and the mapped exit code is 7
    And no vector is counted as passed
