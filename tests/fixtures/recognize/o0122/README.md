# O0122 fixtures and provenance

`sign.cs` is an executable source fixture imported into the current tree IR.
It covers the comparison difference, the shift-or and the nested selection, a
promoted 8-bit operand, and the counterexamples of the work order: the
two-valued `(x >> 31) | 1`, an arithmetic shift of the negation, a sign shift
below width minus one, the swapped difference, unsigned comparisons and
comparisons of two different values.

The formulas are written for this repository from Warren, *Hacker's Delight*,
2nd ed., §2-8 (sourcing rung 3). No upstream implementation is copied.
Independent correctness evidence comes from exhaustive evaluation, Z3 and the
BCL's `Math.Sign`.

The work order's pinned upstream context is:

- [PB-Compiler O0077](https://github.com/Hawkynt/PB-Compiler/blob/0d73acbea185a2597357d53f7ce9acb44758ba76/docs/optimizations/O0077-negation-idioms.md),
  LGPL-3.0-or-later: the forward sign-related negation idioms.
- [PB-Compiler tests/ARITH.BAS](https://github.com/Hawkynt/PB-Compiler/blob/0d73acbea185a2597357d53f7ce9acb44758ba76/tests/ARITH.BAS),
  LGPL-3.0-or-later: the PowerBASIC `SGN` built-in, the target spelling.

They supply context, not expected outputs from Polypiler.
