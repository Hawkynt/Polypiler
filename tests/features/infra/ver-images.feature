Feature: Value images and typed call signatures

  Scenario Outline: A value has exactly one image
    Given the image type "<type>" and the value <value>
    When the value is encoded
    Then the image is "<hex>"
    And decoding "<hex>" as "<type>" gives the value back and consumes all bytes

    Examples:
      | type                    | value                 | hex                        |
      | u32                     | 4294967295            | ffffffff                   |
      | i16                     | -2                    | feff                       |
      | bool                    | true                  | 01                         |
      | f64                     | 1.0                   | 000000000000f03f           |
      | f32                     | -0.0                  | 00000080                   |
      | bigint                  | 0                     | 00000000                   |
      | bigint                  | 255                   | 02000000ff00               |
      | bigint                  | -129                  | 020000007fff               |
      | bigint                  | 18446744073709551616  | 09000000000000000000000001 |
      | utf8                    | "é"                   | 02000000c3a9               |
      | utf16                   | "é"                   | 01000000e900               |
      | utf16                   | U+1F600               | 020000003dd800de           |
      | seq<u16>                | [1, 258]              | 0200000001000201           |
      | opt<i32>                | absent                | 00                         |
      | opt<i32>                | 5                     | 0105000000                 |
      | record{x:u8,y:u32}      | {x: 1, y: 2}          | 0102000000                 |
      | union<null\|i64\|f64>   | the f64 1.5           | 02000000000000f83f         |

  Scenario: Input and output images of a call with by-reference and out parameters
    Given the signature "(a: u32 in, buf: seq<u8> inout, r: u16 out) -> bool"
    And the arguments a = 7 and buf = [1, 2]
    When the function returns true, leaves buf = [9, 2] and sets r = 513
    Then the input image is "07000000020000000102"
    And the output image is "010200000009020102"

  Scenario: A width-less native value takes its width from the signature
    Given the vector file "tests/fixtures/verify/images/u32-identity.json" for a JavaScript function
    When the driver receives the input image "ffffffff" and the function returns the number 4294967296
    Then the outcome is the trap "result-type"
    And no output image is produced

  Scenario Outline: A malformed image or signature is refused
    Given the vector file "tests/fixtures/verify/images/invalid/<file>"
    When it is loaded
    Then loading fails with "PLY0064" and the message contains "<text>"

    Examples:
      | file                   | text                                        |
      | leftover.json          | input image has 1 byte(s) left over         |
      | truncated.json         | is truncated at byte 2                      |
      | bad-bool.json          | byte 02 is not a valid bool                 |
      | bad-tag.json           | tag 3 exceeds the 3 alternatives            |
      | fat-bigint.json        | bigint is not minimal                       |
      | unknown-type.json      | unknown image type 'int'                    |
      | hex-and-trap.json      | both expected and expectedTrap              |
      | missing-signature.json | signature is required                       |

  Scenario: A value that does not fit its type is not encoded
    Given the image type "u8" and the value 256
    When the value is encoded
    Then encoding fails with "PLY0064: value 256 does not fit u8"
