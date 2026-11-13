# O0327 — Replace the linear search of a sorted constant table by a binary search

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0327`.

## What it does

A lookup table of constants is often emitted sorted — opcode values, code points, breakpoints of a
piecewise function — and then searched from the front, because that is the loop everyone writes. The
compiler can see the table: when its entries are strictly ascending, at most ⌈log2 n⌉ probes find a key that
a linear scan needs n/2 comparisons for on average. PB-Compiler implements this for constant integer tables
(`StaticSearchRecognition`). Here the search arrives as `Memory.Find` from `OPT-LOOP-FIND`, so the rule
needs to inspect only the operation's operands. PRD §4.8 (algorithmic replacement), §19.3.

## Example

### Before

```
// global @keys: u16[8] = [3, 17, 42, 56, 91, 100, 120, 200] [readonly]
%f = Memory.Find.u16 @keys, %k, 8
```

### After

```
pre:
  Br probe
probe:                                   // exactly 3 iterations for 8 keys
  %lo   = Phi.u32 [pre: 0], [probe: %lo2]
  %len  = Phi.u32 [pre: 8], [probe: %len2]
  %half = LShr.u32[strict] %len, 1
  %mid  = Add.u32[wrap] %lo, %half
  %e    = Load.u16[inbounds #1] (ElemAddr.ptr<u16>[inbounds #1] @keys, %mid)
  %le   = ICmp.ule.u16 %e, %k
  %lo2  = Select.u32 %le, %mid, %lo
  %len2 = Sub.u32[wrap] %len, %half
  %more = ICmp.ugt.u32 %len2, 1
  Br %more, probe, done
done:
  %e2 = Load.u16[inbounds #1] (ElemAddr.ptr<u16>[inbounds #1] @keys, %lo2)
  %eq = ICmp.eq.u16 %e2, %k
  %f  = Select.u32 %eq, %lo2, 8
```

## When it applies

(1) The searched storage is a module-level `[readonly]` global with a complete constant
initializer ([#121](https://github.com/Hawkynt/Polypiler/issues/121)), searched from its first element; `n` is a constant with `8 ≤ n ≤ length`.
(2) Entries `0 … n−1` are strictly ascending under the comparison of the element type `T` — which is also
the type of the key, fixed by `Memory.Find.<T>` ([#189](https://github.com/Hawkynt/Polypiler/issues/189)); equal neighbours block the rewrite.
(3) The search loop maintains `lo + len ≤ n` and probes index `lo + len/2 < n`, so every load is in bounds
by construction (proof record from the constants). (4) The comparison is `ICmp.ule` for unsigned and
`ICmp.sle` for signed `T`. (5) [#155](https://github.com/Hawkynt/Polypiler/issues/155) prefers ⌈log2 n⌉ probe iterations plus the final test to
`LoopCost` of the linear search at its mean of `n/2` iterations; under the size objective the comparison is
between the two loops' code size.

## How it is proven

`structural+exhaustive-8bit`: with strictly ascending keys the window invariant ("the last entry not above the key, if any, lies in `[lo, lo + len)`") holds through every halving, and the final equality test turns it into first-match-or-`n`; the construction is checked exhaustively for all 8-bit keys and all table lengths 8 … 130 in the oracle. No solver, no timeout path.

## When it must not fire

- A signed table `[-5, 3, 100, …]` probed with an unsigned comparison: −5 is 251 as `u8` and the window moves the wrong way.
- A table with a repeated key `[1, 4, 4, 9, …]`: `Memory.Find` returns the first 4 (index 1); this loop would settle on the last (index 2).
- An unsorted table: the search misses keys that are present.
- A table that is not `[readonly]`: another function may store to it; sortedness is a property of the initializer only.
- `n` larger than the sorted prefix (entries after it are padding zeros): the ascending order breaks inside the searched range.

## Targets

Every target, for tables too large to dispatch on: the loop is a dozen instructions on any CPU
and reads naturally in generated source (targets with a library binary search may map the same shape to
it). On an 8-bit CPU the probe arithmetic is cheap as well. [#155](https://github.com/Hawkynt/Polypiler/issues/155) is the gate — for a table of 8 the
linear scan's four comparisons on average can beat three probes with their bookkeeping on a 6502, and under
`--opt size` the shorter loop wins.

## Switching it off

`--disable-opt O0327`; active at `--opt balanced`, `speed`, `size` and `memory` when the cost model approves; off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` after `OPT-LOOP-FIND` (phase `recognize`) and `OPT-LOOP-KEYSWITCH` (which takes tables of at most 64 keys first), before [#513](https://github.com/Hawkynt/Polypiler/issues/513), which may flatten the probe loop into ⌈log2 n⌉ straight-line steps.
