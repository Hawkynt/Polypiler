# O0170 — Recover a counted iteration from 2-, 4- and 8-way fully unrolled straight-line statements

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#985](https://github.com/Hawkynt/Polypiler/issues/985). Once implemented it is switched off with `--disable-opt O0170`.

## What it does

Hand-optimized and compiler-generated code repeats a statement for consecutive elements instead of
looping: `dst[0] = src[0] ^ k; dst[1] = src[1] ^ k; dst[2] = src[2] ^ k; dst[3] = src[3] ^ k`.
PB-Compiler's loop unrolling produces exactly this, and cipher rounds are written this way by hand. The
repetition fixes a decision — unroll by four — that is wrong for a 6502 (code size) and for AVX (one
vector operation). Recovering `range(0, 4, 1)` plus one body gives the decision back to the target. PRD
§19.3 (de-unroll), §19.5 S1, §22 ("Reconstruct a fully unrolled pure loop"), §23.2 (de-unrolling row),
§24 S1. The recovered loop is a counted body over an `IterationDomain` of [#201](https://github.com/Hawkynt/Polypiler/issues/201) (`range(lo,
hi, step)`); `PolyIteration` in the listings is PRD §22's name for that counted body and is spelled as
[#201](https://github.com/Hawkynt/Polypiler/issues/201) defines it. The original instructions are kept as the fallback graph with a
`RecoveredFrom` link, and the report says "proven equivalent to", never "intended as" (PRD §19.1).

## Example

### Before

```
%p0 = ElementAddr %src, 0
%x0 = Load.u8 %p0
%y0 = Xor.u8 %x0, %key
%q0 = ElementAddr %dst, 0
Store.u8 %q0, %y0
; … the same five instructions with offset 1, with offset 2 and with offset 3
```

### After

```
%dom = IterationDomain range(0, 4, 1)
PolyIteration %dom, λ(%i):
  %p = ElementAddr %src, %i
  %x = Load.u8 %p
  %y = Xor.u8 %x, %key
  %q = ElementAddr %dst, %i
  Store.u8 %q, %y
```

## When it applies

(1) the block contains `k ≥ 2` slices that are pairwise isomorphic as
typed SSA graphs (same opcodes, types, shift contracts, effect classes) under one affine substitution
of offsets and constants in the slice number; operands that are not affine must be the same SSA value
in all slices; (2) the slices are disjoint: no instruction belongs to two slices and no slice uses a
value defined in another, except the accumulator chain link; (3) *order*: if the slices appear
strictly one after another, the loop executes the same instructions in the same order and no memory
fact is needed; if they are interleaved, the loop reorders loads and stores, and [#82](https://github.com/Hawkynt/Polypiler/issues/82) must
prove that no store of slice `i` may alias a load or store of a slice `j ≠ i` — or that the
interleaved order and the loop order see the same values; (4) *traps*: with interleaving, every access
must be proven in bounds or trap-free, because the loop would complete earlier iterations' stores
before a later iteration traps; (5) *effects*: volatile, device and executable-memory accesses are
accepted only in the strictly sequential arrangement, where access order and count are unchanged; (6)
no other instruction with effects lies between the first and the last instruction of the group, unless
the arrangement is sequential and that instruction is not reordered relative to any slice; (7) values
defined in slices and used after the group are rebuilt as loop-exit values (accumulator) or refused
(per-slice temporaries used later).

## How it is proven

`smt-bitvector` translation validation: the original block and the `k`-times
instantiated body are compared on a symbolic memory under the alias facts, for the concrete `k`
(bounded, no induction needed); for the sequential arrangement the two instruction sequences are
identical after substitution and the obligation is structural. Alias, bounds and effect preconditions
are fact obligations; `exhaustive-8bit` interpretation over all placements of `src` and `dst` in a
6-byte buffer for `k` ≤ 4. A failed, unknown or timed-out obligation (budget 5 s each) leaves the IR
unchanged and reports `not applied: proof timeout` or the failing precondition.

## When it must not fire

- Loads-first with overlap: buffer `01 02 03 04 05`, `dst = src + 1`, body `dst[i] = src[i]` (a copy),
  `k = 4`. The original loads `01 02 03 04` first and then stores them: `01 01 02 03 04`. The loop
  interleaves and replicates: `01 01 01 01 01`.
- Observed intermediate writes: stores to a device register are four ordered bus writes — a loop
  preserves that only if the original was sequential; a loads-first original performs all reads before
  any write.
- Trap position: loads-first with the fourth load out of bounds traps before any store; the loop has
  stored three elements by then.
- A gap (offsets 0, 1, 2, 4): no affine substitution with `k = 4`.
- One differing operation: three XORs and one ADD are not an iteration of one body.
- A statement between the slices that reads the destination (a call, a checksum): it observes a
  partially written state at a specific point.

## Switching it off

`--disable-opt O0170`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recover` of [#157](https://github.com/Hawkynt/Polypiler/issues/157), after block-local canonicalization and
alias analysis and before the `recognize` rules are re-run on the result; enables [#281](https://github.com/Hawkynt/Polypiler/issues/281),
[#273](https://github.com/Hawkynt/Polypiler/issues/273), [#277](https://github.com/Hawkynt/Polypiler/issues/277), [#282](https://github.com/Hawkynt/Polypiler/issues/282), [#562](https://github.com/Hawkynt/Polypiler/issues/562) and vectorization. [#155](https://github.com/Hawkynt/Polypiler/issues/155) may
reselect the fallback graph for the target.
