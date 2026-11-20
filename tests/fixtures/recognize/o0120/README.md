# O0120 fixtures and provenance

`minmax.cs` is an executable source fixture imported into the current tree IR.
It covers the mask-select minimum (signed) and maximum (unsigned), the
compare-select minimum, the subtract-sign minimum and maximum of operands
widened from 16 bits, whose difference cannot overflow, and the
counterexamples of the work order: the subtract-sign form on unbounded
operands, a mask from a comparison of another value, a logical sign shift and
a checked subtraction.

The range fact of the work order (`0 ≤ x, y ≤ 65535`) is written as `ushort`
parameters: the frontend's widening conversions carry it into the IR, and the
proof of the actual candidate establishes that the subtraction cannot
overflow. No separate range analysis is consulted.

The formulas are written for this repository from Warren, *Hacker's Delight*,
2nd ed., §2-19, and Anderson, *Bit Twiddling Hacks* (public domain), sourcing
rung 3. No upstream implementation is copied. Independent correctness evidence
comes from exhaustive evaluation, Z3 and the BCL's `Math.Min` and `Math.Max`.

The work order's pinned upstream context is:

- [PB-Compiler O0108](https://github.com/Hawkynt/PB-Compiler/blob/0d73acbea185a2597357d53f7ce9acb44758ba76/docs/optimizations/O0108-branchless-select.md),
  LGPL-3.0-or-later: the forward branchless selection whose output this rule recovers.
- [MediaDroyd Deinterlacer](https://github.com/Hawkynt/MediaDroyd/blob/7f8b6b995cbe54a6afb290a900748393bf01e272/core/player/src/main/kotlin/com/hawkynt/mediadroyd/player/Deinterlacer.kt),
  LGPL-3.0-or-later: `minOf`/`maxOf` chains, the target spelling.

They supply context, not expected outputs from Polypiler.
