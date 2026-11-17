@Layer.core
Feature: Overflow contracts beyond wrap, trap and saturate

  The scenarios run as Polypiler.Tests/Integer/OverflowContractTests.cs. The [checked] text alias is
  withdrawn with the .pir input by ADR 0009; IrIntegerContract.Checked gives its expansion.

  Scenario Outline: An undefined contract is the wrap result in range and no result out of range
    Given a module of the profile "c/23" and the node "<op>" on a and b
    When it is interpreted with a = <a> and b = <b>
    Then the outcome is <outcome>

    Examples:
      | op                                         | a           | b     | outcome                   |
      | Add.i32[undefined]                         | 2147483646  | 1     | 2147483647                |
      | Add.i32[undefined]                         | 2147483647  | 1     | UndefinedBehaviourReached |
      | Mul.i32[undefined]                         | 65536       | 32768 | UndefinedBehaviourReached |
      | Shl.i32[undefined]                         | 1           | 32    | UndefinedBehaviourReached |
      | SDiv.i32[divzero=undefined, ovf=undefined] | 7           | 0     | UndefinedBehaviourReached |
      | SDiv.i32[divzero=undefined, ovf=undefined] | -2147483648 | -1    | UndefinedBehaviourReached |

  Scenario: Only a profile that declares undefinedOverflow permits the contract
    Given the node "Add.i32[undefined]" in a module of the profile "java/21"
    When the module is checked against its profile
    Then it fails with PLY0127 "'Add.i32[undefined]' is not permitted: source profile 'java/21' does not declare undefinedOverflow"

  Scenario Outline: The flag forms return the wrapped value and whether it overflowed
    When "<op>" is interpreted with a = <a> and b = <b>
    Then the value is <value> and the flag is <flag>

    Examples:
      | op              | a          | b   | value       | flag  |
      | AddOverflow.i32 | 2147483647 | 1   | -2147483648 | true  |
      | AddOverflow.u8  | 200        | 100 | 44          | true  |
      | SubOverflow.u8  | 5          | 10  | 251         | true  |
      | MulOverflow.i64 | 3          | 4   | 12          | false |

  Scenario: Checked arithmetic expands per opcode
    When the checked contract of Add and of SDiv is printed
    Then they are "[trap IntegerOverflow]" and "[trap DivideByZero, ovf=trap IntegerOverflow]"

  Scenario Outline: Each profile looks its operator up in one table
    When the contract of "<operator>" on <type> in the profile "<profile>" (<context>) is looked up
    Then it is "<contract>"

    Examples:
      | profile        | context   | operator | type | contract                                      |
      | c/23           | default   | Add      | i32  | [undefined]                                   |
      | c/23           | default   | Add      | u32  | [wrap]                                        |
      | csharp/net10.0 | checked   | Mul      | i64  | [trap IntegerOverflow]                        |
      | csharp/net10.0 | unchecked | SRem     | i32  | [trap DivideByZero, ovf=trap IntegerOverflow] |
      | rust/2024      | debug     | Shl      | u32  | [trap IntegerOverflow]                        |
      | rust/2024      | release   | AShr     | i32  | [mask]                                        |
      | java/21        | default   | SDiv     | i32  | [trap DivideByZero, ovf=wrap]                 |
      | perl/5.40      | default   | Add      | i64  | promote-to-float                              |
