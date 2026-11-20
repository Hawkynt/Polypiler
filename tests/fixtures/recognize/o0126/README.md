# O0126 fixtures and provenance

`divide.cs` is an executable source fixture imported into the current tree IR.
It covers the unsigned reciprocal by a widened multiplication (`/ 10`), the
33-bit add-back reciprocal (`/ 7`), a 16-bit operand multiplied in 32 bits,
the signed reciprocal with its sign correction, the remainder, and a 64-bit
high product through `Math.BigMul` (`/ 3`); and the counterexamples of the
work order: an off-by-one magic, the 16-bit trick on an unbounded 32-bit value,
the signed sequence without its correction and a remainder by another divisor.

The work order's range fact `x ≤ 65535` is written as a `ushort` parameter:
the frontend's widening conversion carries the bound into the IR, and the
certificate reads it from the operand's type. No separate range analysis is
consulted.

The magic numbers are the textbook ones and are checked, not trusted: the rule
derives each divisor and certifies it. The mathematics is Granlund and
Montgomery, "Division by Invariant Integers using Multiplication" (PLDI 1994)
and Warren, *Hacker's Delight*, 2nd ed., ch. 10 (sourcing rung 3). No upstream
implementation is copied. Correctness evidence comes from `BigInteger`
division, exhaustive evaluation at 16 bits and Z3 at 16 bits.

The work order's pinned upstream context is:

- [PB-Compiler O0056](https://github.com/Hawkynt/PB-Compiler/blob/0d73acbea185a2597357d53f7ce9acb44758ba76/docs/optimizations/O0056-reciprocal-division.md),
  LGPL-3.0-or-later: the forward transformation whose output this rule recovers.
- [Cipher OpCodes.js](https://github.com/Hawkynt/Hawkynt.github.io/blob/b3a8a8e92cbca83475986391c3cac56f0b185d29/Cipher/OpCodes.js),
  LGPL-3.0-or-later: `MulHi32`, the high-product helper such sequences are built on.

They supply context, not expected outputs from Polypiler.
