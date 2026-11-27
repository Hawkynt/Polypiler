@Layer.core
Feature: x86-64 CPU feature levels

  Scenario: Level v4 inherits the lower levels
    When the x86-64 profile "x86-64-v4" is selected
    Then it requires "sse2,popcnt,bmi2,avx512vl"
    And it requires XCR0 bits "0xE6"

  Scenario: A higher level does not imply optional extensions
    When the x86-64 profile "x86-64-v4" is selected
    Then it does not require "aes,pclmulqdq,sha,adx,avx512vbmi,avx512vnni"

  Scenario: An unknown level is refused
    When the x86-64 profile "x86-64-v5" is selected
    Then no profile is found
