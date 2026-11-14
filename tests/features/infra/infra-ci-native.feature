@EndToEnd @Layer.native
Feature: Native CI layer

  Scenario Outline: The smoke program compiles and runs under sanitizers
    Given the source "tests/fixtures/infra/ci/native/<file>"
    When it is compiled with "<compiler>" using the sanitized flags and executed
    Then stdout is "38600999"
    And the exit code is 0

    Examples:
      | file        | compiler |
      | adler32.c   | gcc      |
      | adler32.c   | clang    |
      | adler32.cpp | gcc      |
      | adler32.cpp | clang    |

  Scenario: A heap overflow is caught by the address sanitizer
    Given the source "tests/fixtures/infra/ci/native/heap-overflow.c" that reads index 4 of a 4-byte allocation
    When it is compiled with "clang" using the sanitized flags and executed
    Then the exit code is not 0
    And stderr contains "AddressSanitizer: heap-buffer-overflow"

  Scenario: Signed overflow is caught by the undefined-behaviour sanitizer
    Given the source "tests/fixtures/infra/ci/native/signed-overflow.c" that computes 2147483647 + 1 in a volatile int
    When it is compiled with "clang" using the sanitized flags and executed
    Then the exit code is not 0
    And stderr contains "runtime error: signed integer overflow"

  Scenario: A compilation error is a target build failure
    Given the source "tests/fixtures/infra/ci/native/syntax-error.c"
    When it is compiled with "clang" using the sanitized flags
    Then the run outcome is "BuildFailed" with diagnostic "PLY0030"
    And nothing is executed

  # Test design 4: each sanitizer class on the other compiler that has it.
  Scenario Outline: gcc's sanitizers catch the same errors
    Given the source "tests/fixtures/infra/ci/native/<file>" that <does what>
    When it is compiled with "gcc" using the sanitized flags and executed
    Then the exit code is not 0
    And stderr contains "<message>"

    Examples:
      | file              | does what                                         | message                                |
      | heap-overflow.c   | reads index 4 of a 4-byte allocation              | AddressSanitizer: heap-buffer-overflow |
      | signed-overflow.c | computes 2147483647 + 1 in a volatile int         | runtime error: signed integer overflow |

  # Test design 5 to 7: the last valid index, INT_MAX + 0 and the empty input run clean.
  Scenario Outline: The value just inside each boundary runs clean
    Given the source "tests/fixtures/infra/ci/native/<file>"
    When it is compiled with "<compiler>" using the sanitized flags and "<define>" and executed
    Then stdout is "<stdout>"
    And the exit code is 0

    Examples:
      | file              | compiler | define     | stdout     |
      | heap-overflow.c   | clang    | -DINDEX=3  | 4          |
      | heap-overflow.c   | gcc      | -DINDEX=3  | 4          |
      | signed-overflow.c | clang    | -DADDEND=0 | 2147483647 |
      | signed-overflow.c | gcc      | -DADDEND=0 | 2147483647 |
      | adler32.c         | clang    | -DEMPTY    | 1          |
      | adler32.cpp       | gcc      | -DEMPTY    | 1          |

  # Test design 9: warnings are errors.
  Scenario Outline: A warning is a target build failure
    Given the source "tests/fixtures/infra/ci/native/adler32.c"
    When it is compiled with "<compiler>" using the sanitized flags and "-DUNUSED_VARIABLE"
    Then the run outcome is "BuildFailed" with diagnostic "PLY0030"
    And nothing is executed

    Examples:
      | compiler |
      | gcc      |
      | clang    |

  # Test design 10 and 11: what the probe step of the job reports.
  Scenario: An absent compiler fails the probe
    Given the lock fixture "tests/fixtures/infra/ci/native/locks/absent-clang.lock"
    When "clang" is probed against it
    Then stderr contains "error PLY0021: toolchain 'clang' is not installed"
    And the exit code is 1

  Scenario: A compiler at another version fails the probe
    Given the lock fixture "tests/fixtures/infra/ci/native/locks/other-clang.lock" pinning clang 0.0.1
    When "clang" is probed against it
    Then stderr contains "error PLY0022: toolchain 'clang' is 23.1.3, the lock pins 0.0.1"
    And the exit code is 1
