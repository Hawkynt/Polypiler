@Layer.core
Feature: Integer operations with their contract on the node

  The scenarios run as Polypiler.Tests/Integer/IntegerContractTests.cs. A node is an IrIntegerOp; its
  text form is the one IrIntegerOp.Text writes. The .pir text input and its aliases are withdrawn by
  ADR 0009.

  Scenario Outline: Arithmetic follows its overflow contract
    Given the node "<op>" on a and b
    When it is interpreted with a = <a> and b = <b>
    Then the outcome is <result>

    Examples:
      | op                            | a     | b     | result                    |
      | Add.u8[wrap]                  | 200   | 100   | 44                        |
      | Add.i8[trap IntegerOverflow]  | 100   | 100   | the trap IntegerOverflow  |
      | Add.i8[sat]                   | 100   | 100   | 127                       |
      | Sub.u8[sat]                   | 5     | 10    | 0                         |
      | Mul.u16[wrap]                 | 300   | 300   | 24464                     |
      | Mul.i32[trap IntegerOverflow] | 65536 | 32768 | the trap IntegerOverflow  |

  Scenario Outline: Division truncates and guards its edge cases
    Given the node "<op>" on a and b
    When it is interpreted with a = <a> and b = <b>
    Then the outcome is <result>

    Examples:
      | op                                                    | a           | b  | result                   |
      | SDiv.i32[trap DivideByZero, ovf=wrap]                 | -7          | 2  | -3                       |
      | SRem.i32[trap DivideByZero, ovf=wrap]                 | -7          | 2  | -1                       |
      | UDiv.u32[trap DivideByZero]                           | 7           | 0  | the trap DivideByZero    |
      | UDiv.u32[divzero=zero]                                | 7           | 0  | 0                        |
      | URem.u32[divzero=zero]                                | 7           | 0  | 0                        |
      | SRem.i32[divzero=zero, ovf=wrap]                      | 7           | 0  | 0                        |
      | SDiv.i32[trap DivideByZero, ovf=wrap]                 | -2147483648 | -1 | -2147483648              |
      | SDiv.i32[trap DivideByZero, ovf=trap IntegerOverflow] | -2147483648 | -1 | the trap IntegerOverflow |
      | SRem.i32[trap DivideByZero, ovf=wrap]                 | -2147483648 | -1 | 0                        |
      | SRem.i32[trap DivideByZero, ovf=trap IntegerOverflow] | -2147483648 | -1 | the trap IntegerOverflow |

  Scenario Outline: Shifts honour their amount contract
    Given the node "<op>" on x and n
    When it is interpreted with x = <x> and n = <n>
    Then the outcome is <result>

    Examples:
      | op                             | x  | n  | result                    |
      | Shl.u32[mask]                  | 1  | 33 | 2                         |
      | Shl.i32[mask]                  | 1  | -1 | -2147483648               |
      | Shl.u32[unbounded]             | 1  | 32 | 0                         |
      | LShr.u32[unbounded]            | 8  | 33 | 0                         |
      | AShr.i32[unbounded]            | -8 | 33 | -1                        |
      | Shl.i32[unbounded]             | 1  | -1 | 0                         |
      | Shl.u32[trap IntegerOverflow]  | 1  | 31 | 2147483648                |
      | Shl.u32[trap IntegerOverflow]  | 1  | 32 | the trap IntegerOverflow  |
      | Shl.u32[strict]                | 1  | 31 | 2147483648                |
      | Shl.u32[strict]                | 1  | 32 | the trap InvalidOperation |
      | AShr.i32[mask]                 | -8 | 1  | -4                        |
      | LShr.i32[mask]                 | -8 | 1  | 2147483644                |
      | AShr.u8[mask]                  | 128 | 7 | 255                       |

  Scenario: Malformed integer operations are rejected by the verifier
    Given "Add.u32" built without a contract, and "Shl.u32[strict]" with the constant amount 32
    When both modules are verified
    Then the first fails with PLY0127 naming "Add" and the second with PLY0126

  Scenario: Every opcode and contract at eight bits matches the bit-vector definitions
    When every operation and contract is evaluated for all 65536 operand pairs at u8 and i8
    Then there are 0 mismatches with the reference written from SMT-LIB's FixedSizeBitVectors

  Scenario: No backend sees the node
    Given every opcode and contract on every integer type from i8 to u64
    When the module is legalized
    Then it holds no IrIntegerOp and computes on the boundaries of every width what the node computes
