@Layer.core
Feature: Schema and validator for spec/targets/

  Scenario: The valid document passes
    Given the spec tree "tests/fixtures/spec/targets/valid"
    When I run "polypiler-tooling spec validate --root tests/fixtures/spec/targets/valid --family targets"
    Then the exit code is 0
    And the last output line is "1 documents, 0 errors"

  Scenario: A pointerless target declares zero pointer bits
    Given the seed target with pointers "none" and abi.pointerBits 0
    When I change abi.pointerBits to 32 and run "polypiler-tooling spec validate --family targets"
    Then stderr contains "error PLY0046: pointers 'none' requires abi.pointerBits 0, found 32 (rule: pointer-consistency)"
    And the exit code is 1

  Scenario Outline: A document that breaks a rule is refused with the rule's name
    Given the spec fixture "tests/fixtures/spec/targets/invalid/<fixture>"
    When I run "polypiler-tooling spec validate" on a tree containing only that fixture and the valid documents it refers to
    Then stderr contains "error <code>: <message>"
    And the exit code is 1

    Examples:
      | fixture                         | code    | message                                                                                                   |
      | limit-order.yaml                | PLY0046 | limits.exactIntegerBits 64 exceeds limits.maxIntegerBits 53 (rule: limit-order)                           |
      | toolchain-known.yaml            | PLY0046 | toolchain 'nodejs' is not a key of tooling/toolchains.lock (rule: toolchain-known)                        |
      | type-fits-target.yaml           | PLY0046 | TYPES.u64 (64 bits) is mapped without a helper but limits.maxIntegerBits is 53 (rule: type-fits-target)   |
      | unique-type.yaml                | PLY0046 | type 'TYPES.bool' is listed twice (rule: unique-type)                                                     |
      | unique-target                   | PLY0046 | target javascript/es2022 is already defined by TARGETS.javascript.es2022 (rule: unique-target)            |
      | layout-id                       | PLY0046 | id 'TARGETS.javascript.es2022' does not match folder 'node-es2022', expected 'TARGETS.javascript.node-es2022' (rule: layout) |
      | unknown-language.yaml           | PLY0043 | unknown spec id 'LANGUAGES.ecmascript'                                                                    |
      | dependency-without-licence.yaml | PLY0040 | /spec/dependencies/0:                                                                                     |
