@EndToEnd @Layer.core
Feature: Identify data types of untyped JavaScript

  Scenario Outline: An unannotated checksum is imported with integer types
    Given the fixture "js/untyped/<fixture>.mjs" which contains no JSDoc
    When it is imported and "<function>" is explained
    Then the local "<local>" has the type <type>
    And node on the original, the interpreter and node on the re-emitted module return <expected> for <call>

    Examples:
      | fixture | function | local | type | call                          | expected   |
      | adler32 | compute  | a     | U32  | compute(bytes of "Wikipedia") | 300286872  |
      | crc32   | compute  | crc   | U32  | compute(bytes of "123456789") | 3421780262 |
      | fnv1a   | hash     | h     | U32  | hash(bytes of "a")            | 3826002220 |

  Scenario: A parameter used only under coercion is an integer, and the caveat says so
    Given the JavaScript "export function f(a, b) { return (a ^ b) >>> 0; }"
    When it is imported
    Then the parameters a and b have the type I32 and the result U32
    And the module carries the caveat "js-param-integer-inferred" naming a and b

  Scenario: A parameter used in plain arithmetic stays a double
    Given the JavaScript "export function f(a, b) { return a + b; }"
    When it is imported and interpreted with 0.1 and 0.2
    Then the parameters and the result have the type F64
    And the result has the bit pattern 0x3FD3333333333334

  Scenario: A coerced product is not narrowed to an integer multiplication
    Given the JavaScript "export function f(a, b) { a = a | 0; b = b | 0; return (a * b) | 0; }"
    When it is imported and interpreted with 2147483647 and 2147483647
    Then the result is 0, as node returns for the original
    And the IR multiplies in F64 before converting

  Scenario: Math.imul is an integer multiplication
    Given the JavaScript "export function f(a, b) { return Math.imul(a, b); }"
    When it is imported and interpreted with 2147483647 and 2147483647
    Then the result is 1

  Scenario: An unbounded BigInt is refused, a bounded one is a 64-bit integer
    Given the JavaScript "export function f(a) { return a * 1099511628211n; }"
    When it is imported
    Then the import fails with PLY1103
    But "export function f(a) { return BigInt.asUintN(64, a * 1099511628211n); }" with JSDoc "@param {uint64} a" imports with the result type U64

  Scenario: A variable holding a number and an array is refused
    Given the JavaScript "export function f(n) { let x = 0; if (n > 1) x = new Uint8Array(n); return x; }"
    When it is imported
    Then the import fails with PLY1105 naming "x"

  Scenario: A switch section that falls through into statements is refused
    Given a JavaScript switch whose first section has statements and no break
    When it is imported
    Then the import fails with PLY1102 at that section
