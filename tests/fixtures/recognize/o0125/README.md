# O0125 fixtures and provenance

`mulconst.cs` is an executable source fixture imported into the current tree
IR. It covers the add chain, the nested chain, the subtract chain, a negative
coefficient, a promoted 8-bit chain that wraps, and the counterexamples of the
work order: `|` as combiner, a checked addition, two values, a variable shift
amount and a trivial coefficient.

The work order's undefined-overflow (C profile) addition has no counterpart in
the tree IR, whose arithmetic either wraps or carries the checked contract; the
checked case stands for every non-wrapping contract.

The identities are ring arithmetic modulo `2^w` (sourcing rung 3); no upstream
implementation is copied. Independent correctness evidence comes from
exhaustive evaluation, Z3 and the BCL's wrapping multiplication.

The work order's pinned upstream context is:

- [PB-Compiler O0078](https://github.com/Hawkynt/PB-Compiler/blob/0d73acbea185a2597357d53f7ce9acb44758ba76/docs/optimizations/O0078-multiply-decomposition.md),
  LGPL-3.0-or-later: the forward decomposition whose output this rule recovers.
- [PB-Compiler O0004](https://github.com/Hawkynt/PB-Compiler/blob/0d73acbea185a2597357d53f7ce9acb44758ba76/docs/optimizations/O0004-strength-reduction.md),
  LGPL-3.0-or-later: shift-for-multiply strength reduction.

They supply context, not expected outputs from Polypiler.
