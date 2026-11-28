@EndToEnd @Layer.js
Feature: JavaScript toolchain runner

  The JavaScript runner checks and runs JavaScript as written on the locked node, through a driver
  generated for one export and signature; the images it compares are written by hand.

  Scenario Outline: A fixture reproduces its vectors in both module systems
    Given the runner fixture "<file>"
    And a vector file with the export "adler32", the signature "(data: seq<u8> in) -> u32" and the vectors adler32("Wikipedia") = 300286872 and adler32("") = 1
    When RunVectors is executed
    Then the report lists 2 vectors passed and 0 failed
    And the vector "Wikipedia" has the input image "0900000057696b697065646961"
    And its output image is "9803e611"

    Examples:
      | file        |
      | adler32.mjs |
      | adler32.cjs |

  Scenario: One exported function is executed on a list of argument tuples
    Given the runner fixture "add.mjs"
    When ExecuteFunction is called for the export "F" with the signature "(a: i32 in, b: i32 in) -> i32" on the input images "0200000003000000", "ffffffff01000000" and "ffffff7f01000000"
    Then the driver answers exactly "05000000", "00000000" and "!other:RangeError RangeError"
    And the third outcome has the trap class "other:RangeError" and no output image

  Scenario: Wide and special values travel bit-exactly
    Given the runner fixture "wide.mjs"
    When Record is executed for the export "U" with the signature "(x: u64 in) -> u64" on the input images "ffffffffffffffff"
    Then the recorded output images are "ffffffffffffffff"
    When Record is executed for the export "D" with the signature "(x: f64 in) -> f64" on the input images "0000000000000080", "010000000000f87f" and "000000000000f07f"
    Then the recorded output images are "0000000000000080", "000000000000f87f" and "000000000000f07f"
    When Record is executed for the export "B" with the signature "(x: opt<seq<u8>> in) -> opt<seq<u8>>" on the input images "00" and "010100000007"
    Then the recorded output images are "00" and "010100000007"
    And the recorded vector file has the provenance kind "reference-run" and the producer "node"

  Scenario: A buffer the function mutates is compared only as an inout parameter
    Given the runner fixture "xor.mjs"
    When ExecuteFunction is called for the export "X" with the signature "(buf: seq<u8> inout) -> void" on the input images "020000000001"
    Then the driver answers exactly "02000000fffe"
    When ExecuteFunction is called for the export "X" with the signature "(buf: seq<u8> in) -> void" on the input images "020000000001"
    Then the driver answers exactly ""

  Scenario: A wrong expectation is reported, not hidden
    Given the runner fixture "add.mjs" and the vector file "mismatch.vectors.json"
    When RunVectors is executed
    Then the report lists 0 vectors passed and 1 failed
    And the report is explained by the diagnostics PLY0051 and PLY1194 "call 0: expected 6, actual 5"

  Scenario: A result that does not fit its declared type is a trap, never a coerced value
    Given the runner fixture "fraction.mjs"
    When ExecuteFunction is called for the export "F" with the signature "(a: i32 in) -> i32" on the input images "04000000" and "01000000"
    Then the driver answers exactly "02000000" and "!result-type number:0.5"

  Scenario Outline: Failures of the toolchain run are failures of the operation
    Given the runner fixture "<fixture>"
    When <operation> is executed with a wall-time limit of 5 seconds
    Then the operation fails with the diagnostics <codes>
    And no call outcome is returned

    Examples:
      | fixture            | operation       | codes               |
      | broken.js          | Compile         | PLY0030 and PLY1191 |
      | spin.mjs           | ExecuteFunction | PLY0031             |
      | throws-on-load.mjs | ExecuteFunction | PLY1192             |
