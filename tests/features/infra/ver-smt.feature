@Layer.core
Feature: SMT bitvector equivalence prover on Z3

  The prover (#71) asks Z3, through the Microsoft.Z3 binding, whether some input satisfies the
  preconditions, keeps the before side valid and makes the after side invalid or different. Widths
  are tried in ascending order; unsat at every width proves the rewrite, a model refutes it after the
  reference interpreter confirmed it, and a timeout proves nothing. No scenario mocks the solver.

  Scenario: A constant rotation is proved at 16, 32 and 64 bit
    Given the inputs "x: u<w>"
    And the before IR "%a = Shl.u<w>[mask] %x, 3; %s = Sub.u<w> <w>, 3; %b = LShr.u<w>[mask] %x, %s; %r = Or.u<w> %a, %b"
    And the after IR "%r = RotateLeft.u<w> %x, 3"
    When the SMT prover runs at widths 16, 32 and 64 with a 5 s budget
    Then the result is Proved with proof class "smt-bitvector"
    And three queries were answered "unsat"

  Scenario: The overflowing average is refuted with a model whose sum wraps
    Given the inputs "a: u32, b: u32"
    And the before IR "%x = Xor.u32 %a, %b; %h = LShr.u32[mask] %x, 1; %c = And.u32 %a, %b; %r = Add.u32 %c, %h"
    And the after IR "%s = Add.u32 %a, %b; %r = LShr.u32[mask] %s, 1"
    When the SMT prover runs at width 32 with a 5 s budget
    Then the result is Refuted
    And the reported inputs satisfy a + b >= 4294967296
    And the reported before value minus the reported after value is 0x80000000
    And diagnostic PLY0056 is reported

  Scenario: A rewrite that is only right at 8 bit is refuted at 16
    Given the inputs "x: u<w>"
    And the before IR "%r = Mul.u<w> %x, 257"
    And the after IR "%h = Shl.u<w>[mask] %x, 8; %r = Or.u<w> %h, %x"
    When the SMT prover runs at widths 16, 32 and 64 with a 5 s budget
    Then the result is Refuted at width 16
    And substituting the reported x gives different values for "x * 257 mod 65536" and "(x << 8 | x) mod 65536"

  Scenario: An exhausted budget is inconclusive, never a proof
    Given the inputs "a: u64, b: u64"
    And the before IR "%r = Mul.u64 %a, %b"
    And the after IR "%r = Mul.u64 %b, %a"
    When the SMT prover runs at width 64 with a 0 s budget
    Then the result is Inconclusive with reason "timeout"
    And warning PLY0055 is reported with "rewrite not applied"
    And no result of class "smt-bitvector" is recorded

  Scenario: A floating-point obligation is refused
    Given the inputs "a: f64, b: f64"
    And the before IR "%r = Add.f64 %a, %b"
    And the after IR "%r = Add.f64 %b, %a"
    When the SMT prover runs with a 5 s budget
    Then the result is Unsupported
    And diagnostic PLY0062 is reported with mapped exit code 3
