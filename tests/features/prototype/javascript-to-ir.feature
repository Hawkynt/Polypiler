Feature: Import JSDoc-typed JavaScript

  Scenario Outline: Cipher's rotates import and behave like the original
    Given the pinned Cipher file "OpCodes.js"
    When I import it with the export "<function>"
    Then the import succeeds
    And the interpreter returns <expected> for <function>(<value>, <positions>)
    And node returns <expected> for the original OpCodes.<function>(<value>, <positions>)

    Examples:
      | function | value      | positions | expected   |
      | RotL32   | 0x80000001 | 1         | 3          |
      | RotL32   | 0x12345678 | 8         | 878082066  |
      | RotL32   | 0x12345678 | 32        | 305419896  |
      | RotR32   | 0x00000003 | 1         | 2147483649 |
      | RotR32   | 0x12345678 | 0         | 305419896  |

  Scenario Outline: A JavaScript checksum survives the round trip through the IR
    Given the fixture "<fixture>.mjs"
    When I run "polypiler translate <fixture>.mjs --from javascript --to javascript --output out"
    Then the exit code is 0
    And node evaluates "<call>" on the generated module and on the original to <expected>

    Examples:
      | fixture | call                                 | expected   |
      | adler32 | compute(bytes of "Wikipedia")        | 300286872  |
      | crc32   | compute(bytes of "123456789")        | 3421780262 |
      | fnv1a   | hash(bytes of "a")                   | 3826002220 |

  Scenario: An uncoerced sum is refused
    Given the JavaScript "/** @param {int32} a @param {int32} b @returns {int32} */ function f(a, b) { return a + b; }"
    When it is imported
    Then the import fails with PLY1103 at line 1
    And no IR function is produced

  Scenario: A coerced sum wraps
    Given the JavaScript "/** @param {int32} a @param {int32} b @returns {int32} */ function f(a, b) { return (a + b) | 0; }"
    When it is imported and interpreted with 2147483647, 1
    Then the result is -2147483648

  Scenario: Plain multiplication is refused, Math.imul is accepted
    Given the JavaScript "/** @param {int32} a @param {int32} b @returns {int32} */ function f(a, b) { return (a * b) | 0; }"
    When it is imported
    Then the import fails with PLY1103 naming "a * b"
    But the same function written with "Math.imul(a, b)" imports and returns 131073 for 65537, 65537, exactly as node does

  Scenario: A function without JSDoc types is refused
    Given the JavaScript "function f(a) { return a | 0; }"
    When it is imported
    Then the import fails with PLY1101 naming parameter "a"

  Scenario: An unknown export name is an error
    When I import "OpCodes.js" with the export "NoSuchFunction"
    Then the import fails with PLY1104
