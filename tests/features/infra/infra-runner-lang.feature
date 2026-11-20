@Layer.core
Feature: Language runner contract

  Scenario: A driver reports return images and traps in order
    Given the function driver outputs "03000000;!divide-by-zero DivideByZeroException"
    When the runner reads the output for 2 signed 32-bit results
    Then the first result is the image "03000000"
    And the second result is the trap "divide-by-zero"

  Scenario Outline: Invalid driver output fails the operation
    Given the function driver outputs "<stdout>"
    When the runner reads the output for 2 signed 32-bit results
    Then reading the driver output fails with "<reason>"

    Examples:
      | stdout                | reason                |
      | 03000000              | 1 line(s) for 2 calls |
      | 03000000;hello        | not an image          |
      | 03000000;0300000      | odd number of hex     |
      | 03000000;!nonsense X  | is not a trap class   |
