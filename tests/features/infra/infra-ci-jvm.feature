@EndToEnd @Layer.jvm
Feature: Kotlin/Java CI layer

  Scenario Outline: The smoke program compiles, runs and prints the checksum
    Given the source "tests/fixtures/infra/ci/jvm/<file>"
    When it is compiled with "<compiler>" and executed on the pinned JDK
    Then stdout is "38600999"
    And the exit code is 0

    Examples:
      | file         | compiler |
      | Adler32.java | javac    |
      | adler32.kt   | kotlinc  |

  Scenario: Java and Kotlin agree on 32-bit wrap-around and unsigned shift
    Given the sources "Wrap.java" and "wrap.kt" printing "Integer.MAX_VALUE + 1" and "-1 >>> 28" (Kotlin: "-1 ushr 28")
    When both are compiled and executed
    Then each prints "-2147483648" followed by "15"
    And the two outputs are byte-identical

  Scenario Outline: A type error is a target build failure
    Given the source "tests/fixtures/infra/ci/jvm/<file>"
    When it is compiled with "<compiler>"
    Then the run outcome is "BuildFailed" with diagnostic "PLY0030"
    And the compiler output contains "<text>"

    Examples:
      | file           | compiler | text                |
      | TypeError.java | javac    | incompatible types  |
      | type-error.kt  | kotlinc  | type mismatch       |

  Scenario: An uncaught exception is a failed execution, not a build failure
    Given the source "tests/fixtures/infra/ci/jvm/Throws.java"
    When it is compiled with "javac" and executed
    Then the run outcome is "Completed" with exit code 1
    And stderr contains "Exception in thread"
