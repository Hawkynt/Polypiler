# O0110 fixtures and provenance

`parity.cs` is an executable source fixture imported into the current tree IR.
It covers the exclusive-or fold of a 32-bit word (logical and arithmetic
shifts), the fold to a nibble followed by the parity table `0x6996` at 8 and 32
bits, and the counterexamples of the work order: a fold without the `>> 16`
step, the complement table `0x9669` and a final mask of 3.

The formulas are written for this repository from Warren, *Hacker's Delight*,
2nd ed., §5-2 (sourcing rung 3). No upstream implementation is copied; the
table `0x6996` is the constant of that section (entry `v` at bit `v` is the
parity of `v`), copied as a constant. Independent correctness evidence comes
from exhaustive evaluation, Z3 and the BCL's `BitOperations.PopCount`.

The work order's pinned upstream context is:

- [Cipher checksum/parity.js](https://github.com/Hawkynt/Hawkynt.github.io/blob/b3a8a8e92cbca83475986391c3cac56f0b185d29/Cipher/algorithms/checksum/parity.js),
  LGPL-3.0-or-later: odd, even and longitudinal parity over byte data.
- [Cipher OpCodes.js](https://github.com/Hawkynt/Hawkynt.github.io/blob/b3a8a8e92cbca83475986391c3cac56f0b185d29/Cipher/OpCodes.js),
  LGPL-3.0-or-later: `LFSRStep`, feedback as the parity of masked taps.

They supply context, not expected outputs from Polypiler.
