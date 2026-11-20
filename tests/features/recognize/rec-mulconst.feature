@Layer.core
Feature: Proved shift-add multiplication recognition
  Source fixtures are imported into the current tree IR before recognition.
  The coefficient is computed modulo 2^w; only wrapping arithmetic takes part.

  Scenario Outline: A shift-add chain over one value becomes a multiplication
    Given the arithmetic recognition fixture "o0125/mulconst.cs" function "<form>"
    When only arithmetic rule O0125 runs with proof budget 5 seconds
    Then the arithmetic result contains a multiplication by <c> and an SMT proof
    And both arithmetic versions multiply 00000007 to <small>
    And both arithmetic versions multiply 80000001 to <wrapped>

    Examples:
      | form       | c          | small    | wrapped  |
      | Ten        | 10         | 00000046 | 0000000A |
      | TenNested  | 10         | 00000046 | 0000000A |
      | Fifteen    | 15         | 00000069 | 8000000F |
      | MinusThree | 4294967293 | FFFFFFEB | 7FFFFFFD |

  Scenario: A promoted byte chain wraps as the multiplication does
    Given the arithmetic recognition fixture "o0125/mulconst.cs" function "NarrowTen"
    When only arithmetic rule O0125 runs with proof budget 5 seconds
    Then the arithmetic result contains a multiplication by 10 and an SMT proof
    And both arithmetic versions multiply C8 to D0

  Scenario: Or in place of Add loses carries and is not a multiplication
    Given the arithmetic recognition fixture "o0125/mulconst.cs" function "OrTen"
    When only arithmetic rule O0125 runs with proof budget 5 seconds
    Then the arithmetic IR is unchanged
    And the arithmetic report contains "O0125 not applied: combiner is not Add or Sub"
    And both arithmetic versions multiply 00000005 to 0000002A

  Scenario: Checked addition is not rewritten
    Given the arithmetic recognition fixture "o0125/mulconst.cs" function "CheckedTen"
    When only arithmetic rule O0125 runs with proof budget 5 seconds
    Then the arithmetic IR is unchanged
    And the arithmetic report contains "O0125 not applied: non-wrapping arithmetic"

  Scenario: Two different values are not one multiplication
    Given the arithmetic recognition fixture "o0125/mulconst.cs" function "TwoValues"
    When only arithmetic rule O0125 runs with proof budget 5 seconds
    Then the arithmetic IR is unchanged
    And the arithmetic report contains "O0125 not applied: more than one variable"

  Scenario: The switch disables the rule and its reports
    Given the arithmetic recognition fixture "o0125/mulconst.cs" function "Ten"
    When arithmetic rule O0125 is disabled
    Then the arithmetic IR is unchanged
    And the arithmetic report is empty

  Scenario: A proof timeout leaves the chain untouched
    Given the arithmetic recognition fixture "o0125/mulconst.cs" function "Ten"
    When only arithmetic rule O0125 runs with proof budget 0 seconds
    Then the arithmetic IR is unchanged
    And the arithmetic report contains "O0125 not applied: proof timeout"
