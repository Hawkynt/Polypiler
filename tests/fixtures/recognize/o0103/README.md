# O0103 fixtures and provenance

`zerocount.cs` is an executable source fixture imported into the current tree
IR. It covers the guarded trailing-zero loop, the unguarded trailing-zero loop
after an early return for zero, the bit-length loop and the guarded top-down
leading-zero loop, and the counterexamples of the work order: the unguarded
loop whose input may be zero (it does not end for zero), the bit-length loop
with an arithmetic shift (it does not end for a negative value), a bound other
than the width, a scan that starts at bit 1 and a body with a store.

The work order's non-zero range fact is written as the dominating branch
`if (x == 0) return 32;`. The rule reads that branch, or an enclosing
`if (x != 0)`, directly; no separate range analysis is consulted.

The loops are textbook (Warren, *Hacker's Delight*, 2nd ed., §5-3 and §5-4;
sourcing rung 3). No upstream implementation is copied. Independent
correctness evidence comes from exhaustive evaluation, Z3 and the BCL's
`BitOperations.TrailingZeroCount` and `LeadingZeroCount`.

The work order's pinned upstream context is:

- [Cipher lzma.js](https://github.com/Hawkynt/Hawkynt.github.io/blob/b3a8a8e92cbca83475986391c3cac56f0b185d29/Cipher/algorithms/compression/lzma.js),
  LGPL-3.0-or-later: range-coder normalization, the consumer of leading-zero
  counts.

It supplies context, not expected outputs from Polypiler.
