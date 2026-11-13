# O0205 — Recover a bit routing from a multiplication after an exact carry proof

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#546](https://github.com/Hawkynt/Polypiler/issues/546). Once implemented it is switched off with `--disable-opt O0205`.

## What it does

Multiply-and-mask bit hacks — spread one bit over a byte, gather spaced bits, reverse a byte with three
multiplications — are routings wearing arithmetic. PRD §24 Q1 requires reverse-recognizing "a verified
multiplication-based routing"; PRD §20.3 step 3 and §20.7 set the condition: the actual modular multiply
with carries, never an OR of shifted copies, and no rewrite "when carries can affect selected outputs".
PRD §22: "any carry-caused counterexample rejects the routing equivalence".

## Example

### Before

```
%a = And.u8 %x, 0x11
%r = Mul.u8 %a, 0x09
```

### After

```
%r = BitRoute.u8 %x, [0,=0,=0,0,4,=0,=0,4]
```

## When it applies

the slice of the root ([#541](https://github.com/Hawkynt/Polypiler/issues/541)) contains at least one `Mul` by a
constant at a width ≤ 64. For each such product the operand's origins are a routing without `One` bits;
the possible-non-zero bits form the pre-mask; a product column yields an origin only if it is **clean**
(at most one contributor and no carry can reach it, [#531](https://github.com/Hawkynt/Polypiler/issues/531)) — every other column is `Mixed`.
The root's origins contain no `Mixed` bit (masks, shifts and truncations after the product may discard
the unclean columns). Each product also passes `MultiplyRoutingProof.Prove`.

## How it is proven

the whole slice against the replacement: `exhaustive-<n>bit` for inputs up to 24 bits,
else `smt-bitvector` with `bvmul`; the report also names the per-product class (`carry-free-columns`).
Any verdict but proved — including a timeout — leaves the IR unchanged.
- **Counterexamples** (values where carries overlap, PRD §23.4):
- `((x & 0x03) · 0x03) & 0x05`: for `x = 0x03` the product is `0x09`, masked `0x01`; the routing "bit 0 → 0, bit 1 → 2" would give `0x05`.
- `((x & 0x1111) · 0x1111) & 0x4000` at 32 bits: no partial product lands on bit 14, yet `x = 0x1111` gives `0x01234321` with bit 14 set.
- `x · 0x09` without a pre-mask at 8 bits: `x = 0xFF` gives `0xF7`; every column above bit 2 collides.
- A product by a variable.

## Switching it off

`--disable-opt O0205`; active at `--opt balanced`, `speed`, `size`, `memory`; off at `none`.

## Interactions with other passes

runs after constant folding and after [#572](https://github.com/Hawkynt/Polypiler/issues/572) has had its turn on arithmetic
multiplications; feeds the naming rules and [#530](https://github.com/Hawkynt/Polypiler/issues/530), which may choose a non-multiply network for
targets where multiplication is slow or absent.
