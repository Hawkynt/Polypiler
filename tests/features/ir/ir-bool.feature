@Layer.core
Feature: Booleans and their integer encodings

  The scenarios run as Polypiler.Tests/Integer/BooleanEncodingTests.cs. BoolToInt and IntToBool are
  IrBooleans.ToInt and IrBooleans.FromInt, built from the conditional and the comparisons every
  backend writes; the .pir text form is withdrawn by ADR 0009.

  Scenario Outline: Truth-value encodings convert both ways
    Given "IntToBool[<rule>]" of x of type <type> and "BoolToInt.<type>[true=<k>]" of its result
    When it is interpreted with x = <x>
    Then the integer is <i>

    Examples:
      | rule    | type | k  | x   | i  |
      | nonzero | i16  | -1 | 5   | -1 |
      | nonzero | i16  | -1 | 0   | 0  |
      | nonzero | u8   | 1  | 255 | 1  |
      | eq -1   | i16  | -1 | 5   | 0  |
      | eq -1   | i16  | -1 | -1  | -1 |

  Scenario: BASIC NOT on a flag and logical Not differ
    Given x = 5 of type i16
    When its bitwise complement and "BoolToInt.i16[true=-1] (LogicalNot (IntToBool[nonzero] x))" are interpreted
    Then the bitwise result is -6 and the logical result is 0

  Scenario: A branch on an integer is rejected
    Given an if statement whose condition is a u32
    When the module is verified
    Then verification fails with PLY0100

  Scenario: Arithmetic on bool is rejected
    Given "Add" on two bool operands, as an operator and as an integer operation with a contract
    When the module is verified
    Then verification fails with PLY0130 naming "Add"

  Scenario: A zero true-encoding is rejected
    When "BoolToInt.u8[true=0]" is built
    Then it is refused with PLY0131

  Scenario: A stored boolean uses the encoding of its field
    Given a storage whose default is C's int flag and whose field "Visible" is stored as BASIC's -1 in i16
    When a true value is stored in "Visible" and read back
    Then the stored integer is -1 and the value read is true
