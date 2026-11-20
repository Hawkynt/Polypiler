# O0121 fixtures and provenance

`absolute.cs` is an executable source fixture imported into the current tree IR.
It covers both sign-mask forms, compare-select, a promoted 8-bit result and the
logical-shift, wrong-shift, checked and different-mask counterexamples.

The formulas are written for this repository from Warren, *Hacker's Delight*,
2nd ed., §2-4 (sourcing rung 3). No upstream implementation is copied.
Independent correctness evidence comes from exhaustive evaluation, Z3 and BCL
`BigInteger.Abs` followed by the declared signed wrapping conversion.

The work order's pinned upstream context is:

- [MediaDroyd Deinterlacer](https://github.com/Hawkynt/MediaDroyd/blob/7f8b6b995cbe54a6afb290a900748393bf01e272/core/player/src/main/kotlin/com/hawkynt/mediadroyd/player/Deinterlacer.kt),
  blob `49a47ecbacfbdf461616f09851a243ca5a7f0eb9`, LGPL-3.0-or-later.
  `MediaDroydDelta` extracts the scalar contract `abs(previous - current)` used
  by the temporal pixel-difference calculation and spells its magnitude with
  the sign-mask idiom. Pixel operands are in 0..255. This is a derived scalar
  fixture, not a Kotlin import or execution of the deinterlacer.
- [PB-Compiler O0077](https://github.com/Hawkynt/PB-Compiler/blob/0d73acbea185a2597357d53f7ce9acb44758ba76/docs/optimizations/O0077-negation-idioms.md),
  LGPL-3.0-or-later. Its `0 - x` forward negation is the compare-select spelling
  in `CompareSelect`. It supplies context for the reverse recognition, not
  expected outputs from Polypiler.

Both pinned licences were checked. Generated-code size and timing are recorded
in the pull request, without a speedup gate; target cost selection is outside
O0121. The complete tests also exercise every signed width and all mirrored
operator spellings independently of this small source fixture.
