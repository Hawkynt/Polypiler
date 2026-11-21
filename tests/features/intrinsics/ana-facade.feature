@Layer.core
Feature: Fact query facade

  One object answers facts of any domain about a value at a program point of the structured IR (#88): the
  point before a statement of a block, or the end of a block; the entry of a branch is the edge into it. A
  domain no analysis provides answers its Unknown, never an error and never a proof, and the soundness
  checker holds every answered fact against real runs of the interpreter. The sources are C# methods.

  Scenario: A registered domain answers
    Given the analysed C# method "public static uint F(uint x) { uint a = 2 + 3; uint b = a * a; return b; }"
    And the constant domain is registered
    When the constant fact of "b" is queried before the return
    Then the fact is "constant 25"

  Scenario: A missing domain yields Unknown, not an error and not a proof
    Given the analysed C# method "public static uint F(uint x) { uint a = x + 1; return a; }"
    And no provider is registered
    When the constant fact of "a" is queried before the return
    Then the fact is "unknown"
    And Decide for "a < 10" before the return is Unknown

  Scenario: Require reports the missing domain
    Given the analysed C# method "public static uint F(uint x) { uint a = x + 1; return a; }"
    And no provider is registered
    When Require is called for the known-bits fact of "a" before the return
    Then it fails with "error PLY0300: no analysis provides facts of domain 'known-bits'"

  Scenario: Facts are per program point
    Given the analysed C# method "public static uint F(uint x) { uint y = 0; if (x == 7) { y = x + 1; } else { y = 2; } return y; }"
    And the constant domain is registered
    When the constant fact of "x" is queried at the entry of the then branch and of the else branch
    Then the fact at the then branch is "constant 7"
    And the fact at the else branch is "unknown"

  Scenario: The checker catches an unsound provider
    Given the analysed C# method "public static uint F(uint x) { uint a = x + 1; return a; }"
    And a deliberately wrong provider that claims "a" is constant 4 before the return
    When FactChecker runs the function with x = 5
    Then it fails with "error PLY0302: unsound fact: constant claims constant 4 for a at block 0, statement 1, observed 6"

  Scenario: A value cannot be queried where it is not defined
    Given the analysed C# method "public static uint F(bool c, uint x) { if (c) { uint v = x + 1; x = v; } else { x = 2; } return x; }"
    And the constant domain is registered
    When the constant fact of "v" is queried at the entry of the else branch
    Then it fails with "'v' is not defined at block 2, statement 0"
