@Layer.js
@EndToEnd
Feature: JS/TS CI layer

  Scenario: The smoke module runs and its test passes
    Given the module "tests/fixtures/infra/ci/js/adler32.mjs" and its test "adler32.test.mjs"
    When I run "node adler32.mjs" and "node --test adler32.test.mjs" in the sandbox
    Then the first command prints "38600999"
    And the second command exits with 0 and reports "pass 1"

  Scenario: 32-bit coercions behave as ECMAScript defines
    Given the module "tests/fixtures/infra/ci/js/coercion.mjs" printing "(0xFFFFFFFF | 0)" and "(-1 >>> 0)"
    When I run "node coercion.mjs" in the sandbox
    Then stdout is "-1" followed by "4294967295"

  Scenario: The TypeScript checker accepts the typed smoke module
    Given the project "tests/fixtures/infra/ci/js" with "adler32.ts" and a strict "tsconfig.json"
    When I run "tsc --noEmit -p ." in the sandbox
    Then the exit code is 0
    And no ".js" file is written

  Scenario: A failing test fails the run
    Given the test "tests/fixtures/infra/ci/js/failing.test.mjs" asserting that 1 equals 2
    When I run "node --test failing.test.mjs" in the sandbox
    Then the exit code is 1
    And the output reports "fail 1"

  # TypeScript 7, the native checker the lock pins, ends a check that found errors and emitted nothing
  # with 1 (diagnostics present, outputs skipped); the JavaScript-hosted checkers before it returned 2.
  Scenario: A type error fails the checker
    Given the file "tests/fixtures/infra/ci/js/type-error.ts" containing "const n: number = \"abc\";"
    When I run "tsc --noEmit type-error.ts" in the sandbox
    Then the exit code is 1
    And stdout contains "error TS2322"
