Feature: Property-based test harness with fixed seeds and shrinking

  Scenario Outline: A seed produces the published SplitMix64 sequence
    Given a seeded random generator with seed <seed>
    When three 64-bit values are drawn
    Then they are <first>, <second> and <third>

    Examples:
      | seed    | first               | second              | third               |
      | 0       | 0xE220A8397B1DCDAF  | 0x6E789E6AA1B965F4  | 0x06C45D188009454F  |
      | 1234567 | 6457827717110365317 | 3203168211198807973 | 9817491932198370423 |

  Scenario Outline: Integer sequences start with the edge values of their width
    Given the bit-pattern sequence for width <width> with seed 7
    When the first values are taken
    Then they are <edges>

    Examples:
      | width | edges                                                                   |
      | 1     | 0x0, 0x1                                                                |
      | 8     | 0x00, 0x01, 0x7F, 0x80, 0xFE, 0xFF                                      |
      | 16    | 0x0000, 0x0001, 0x7FFF, 0x8000, 0xFFFE, 0xFFFF                          |
      | 64    | 0x0, 0x1, 0x7FFFFFFFFFFFFFFF, 0x8000000000000000, 0xFFFFFFFFFFFFFFFE, 0xFFFFFFFFFFFFFFFF |

  Scenario: A falsified property is shrunk to the smallest failing value and recorded
    Given the property "x < 100" over 8-bit patterns with seed 7 and 1000 inputs
    When the property is checked
    Then it fails with PLY0071 naming the shrunk value 0x64 and the original value 0x7F at input index 2
    And "artifacts/properties/" contains a record with seed 7, shrunk "0x64" and original "0x7F"

  Scenario: A property that holds runs exactly the requested number of inputs
    Given the property "(x ^ x) == 0" over 32-bit patterns with seed 7 and 1000 inputs
    When the property is checked
    Then it passes after 1000 inputs
    And no failure record is written

  Scenario: A property without inputs to check is refused
    Given the property "x == x" over 8-bit patterns with seed 7 and 0 inputs
    When the property is checked
    Then an ArgumentOutOfRangeException names the parameter "count"
