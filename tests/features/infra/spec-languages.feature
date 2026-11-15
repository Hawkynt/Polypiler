@Layer.core
Feature: Schema and validator for spec/languages/

  Scenario: The valid document passes
    Given the spec tree "tests/fixtures/spec/languages/valid"
    When I run "polypiler-tooling spec validate --root tests/fixtures/spec/languages/valid --family languages"
    Then the exit code is 0
    And the last output line is "2 documents, 0 errors"

  Scenario: Every command-line language identifier can be declared
    Given language documents for the identifiers "csharp", "javascript", "powerbasic" and "cbmbasic" with diagnostic blocks 1000, 1100, 1200 and 2700
    When I run "polypiler-tooling spec validate --family languages"
    Then the exit code is 0

  Scenario Outline: A document that breaks a rule is refused with the rule's name
    Given the spec fixture "tests/fixtures/spec/languages/invalid/<fixture>"
    When I run "polypiler-tooling spec validate" on a tree containing only that fixture and the valid documents it refers to
    Then stderr contains "error <code>: <message>"
    And the exit code is 1

    Examples:
      | fixture                   | code    | message                                                                                                     |
      | diagnosed-needs-code.yaml | PLY0046 | construct 'with statement' is diagnosed but names no diagnostic (rule: diagnosed-needs-code)                |
      | diagnostic-in-block.yaml  | PLY0046 | diagnostic PLY1250 is outside the frontend range PLY1100-PLY1149 of javascript (rule: diagnostic-in-block) |
      | toolchain-known.yaml      | PLY0046 | toolchain 'nodejs' is not a key of tooling/toolchains.lock (rule: toolchain-known)                          |
      | unique-identifier         | PLY0046 | diagnosticBlock 1100 is already used by LANGUAGES.javascript (rule: unique-identifier)                      |
      | profile-has-language.yaml | PLY0046 | profile in folder 'javascript' names language 'LANGUAGES.typescript' (rule: profile-has-language)          |
      | profile-without-language  | PLY0043 | unknown spec id 'LANGUAGES.javascript'                                                                      |
      | layout                    | PLY0046 | path 'languages/javascript.yaml' is neither languages/<identifier>/language.yaml                            |
