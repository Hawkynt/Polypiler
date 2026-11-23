@Layer.core
Feature: Scalar evolution
  Loops are C# source imported into the structured tree IR. An evolution is read where a pass starts,
  before the loop's test; a trip count is how often the body runs.

  Scenario: A canonical counter
    Given the analysed C# loop "public static uint F() { uint s = 0; for (uint i = 0; i < 100; i++) s += i; return s; }"
    When scalar evolution runs
    Then the evolution of "i" is "AddRec(start 0, step 1) nuw nsw"
    And the trip count of the loop is "Exact(100)"

  Scenario: Start and step other than 0 and 1
    Given the analysed C# loop "public static uint F() { uint s = 0; for (uint i = 10; i < 20; i += 3) s++; return s; }"
    When scalar evolution runs
    Then the evolution of "i" is "AddRec(start 10, step 3) nuw nsw"
    And the trip count of the loop is "Exact(4)"

  Scenario: An affine expression of the counter is an AddRec
    Given the analysed C# loop "public static uint F() { uint s = 0; for (uint i = 0; i < 100; i++) { uint o = i << 2; uint a = o + 2; s += a; } return s; }"
    When scalar evolution runs
    Then the evolution of "a" before statement 2 of the body is "AddRec(start 2, step 4) nuw nsw"

  Scenario: A symbolic bound gives a symbolic trip count
    Given the analysed C# loop "public static uint F(uint len) { uint s = 0; for (uint i = 0; i < len; i++) s++; return s; }"
    When scalar evolution runs
    Then the trip count of the loop is "Symbolic(len, 0)"

  Scenario: A non-affine recurrence is unknown
    Given the analysed C# loop "public static uint F() { uint x = 1; for (uint i = 0; i < 10; i++) x = x * 3; return x; }"
    When scalar evolution runs
    Then the evolution of "x" is "Unknown"

  Scenario: A step that can jump over an inequality exit is not counted
    Given the analysed C# loop "public static int F() { int s = 0; for (uint i = 0; i != 10; i += 3) { s++; if (s > 100) return s; } return s; }"
    When scalar evolution runs
    Then the trip count of the loop is "Unknown"
