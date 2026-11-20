# O0108 fixtures and provenance

`bits.cs` is an executable source fixture imported into the current tree IR.
It covers the bit test by shift-and-mask (logical and arithmetic shift) and by
mask-and-compare, set, clear and flip with a variable position, a promoted
8-bit set, and the counterexamples of the work order: a mask shifted at 32 bits
and sign-extended to 64, a shifted constant 2 and a two-bit field.

The tree IR has one shift contract: the count is taken modulo the width, as
JavaScript, C# and Java shift (`1u << 33` is `1u << 1`). The work order's
`[strict]` shift and its range fact `0 ≤ n ≤ w-1` therefore have no
counterpart here; the recognized operation takes the position `n & (w - 1)`,
which keeps that modulo behaviour and is always within the width.

The identities are elementary (sourcing rung 3); no upstream implementation is
copied. Independent correctness evidence comes from exhaustive evaluation, Z3
and the BCL's shift operators on the original source.

The work order's pinned upstream context is:

- [Cipher OpCodes.js](https://github.com/Hawkynt/Hawkynt.github.io/blob/b3a8a8e92cbca83475986391c3cac56f0b185d29/Cipher/OpCodes.js),
  LGPL-3.0-or-later: `GetBit` as `((value >>> bitIndex) & 1) !== 0` and
  `SetBit` as `value | (1 << bitIndex)` / `value & ~(1 << bitIndex)`.
- [PB-Compiler tests/LOWLEVEL.BAS](https://github.com/Hawkynt/PB-Compiler/blob/0d73acbea185a2597357d53f7ce9acb44758ba76/tests/LOWLEVEL.BAS),
  LGPL-3.0-or-later: `BIT(s, 2)`, the target-side spelling.

They supply context, not expected outputs from Polypiler.
