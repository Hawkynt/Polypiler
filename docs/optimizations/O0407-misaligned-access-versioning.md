# O0407 — Select an aligned copy of a vector loop by a run-time test of its stream addresses

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0407`.

## What it does

`OPT-VEC-ALIGNPEEL` makes one stream of a vector loop aligned by running scalar iterations first. It
cannot align a second stream with a different residue, it cannot align an array whose base is not a
multiple of its element size, and for short loops the peel costs more than it gains. Where the arrays are
usually aligned anyway — heap blocks, static tables — one test of the low address bits of every stream
selects a copy of the vector loop in which all accesses are provably aligned, and the unaligned copy remains
for the rest. PB-Compiler splits this out of its O0139 as O0251. PRD §4.8 (SIMD vectorization), §8 P3.

## Example

### Before

```
vloop:                                        // output of O0020; %a, %b, %c of unknown alignment
  %vx = VecLoad.vec<u8, 16>[inbounds #1] %a, %i
  %vy = VecLoad.vec<u8, 16>[inbounds #2] %b, %i
  %vz = Xor.vec<u8, 16> %vx, %vy
  VecStore.vec<u8, 16>[inbounds #3] %c, %i, %vz
tail:
```

### After

```
g1:
  %ta = ICmp.eq.uptr (And.uptr (PtrToInt.uptr[expose] %a0), 15), 0     // %a0 = address of the first element the loop reads from %a
  Br %ta, g2, vloop
g2:
  %tb = ICmp.eq.uptr (And.uptr (PtrToInt.uptr[expose] %b0), 15), 0
  Br %tb, g3, vloop
g3:
  %tc = ICmp.eq.uptr (And.uptr (PtrToInt.uptr[expose] %c0), 15), 0
  Br %tc, vloop.aligned, vloop
vloop.aligned:                                // clone of vloop; ANA-ALIGNMENT: every access address is a multiple of 16
vloop:                                        // unchanged
tail:                                         // shared by both copies
```

## When it applies

(1) The function contains the vector loop and scalar tail of [#510](https://github.com/Hawkynt/Polypiler/issues/510) and `OPT-VEC-ALIGNPEEL` did not
apply to it. (2) Each tested address is that of the **first element the vector loop accesses** in its
stream (`base + lo × size` for an index starting at `lo`), not the base of the array. (3) The vector step
in bytes (`VF × size`) is a multiple of the alignment `A`, so alignment at entry implies alignment in every
iteration ([#83](https://github.com/Hawkynt/Polypiler/issues/83) propagation through `PtrAdd`). (4) One conditional branch per stream, each on
the mask test `ICmp.eq(And(PtrToInt p, A − 1), 0)`; a combined test on the OR of the addresses is not used
because the analysis refines per edge. A stream whose alignment is already `True` gets no test; one that
is proven `False` blocks the rule. (5) The profile can take addresses and declares `alignedPerBlock`
(precondition 5 of `OPT-VEC-ALIGNPEEL`). (6) The clone is an exact copy; it differs from the general
version only in the facts available to [#499](https://github.com/Hawkynt/Polypiler/issues/499). (7) [#155](https://github.com/Hawkynt/Polypiler/issues/155) accepts the added size for the
aligned gain over the proven or assumed trip count ([#350](https://github.com/Hawkynt/Polypiler/issues/350)); the rule is off at `size` and `memory`.
(8) A vector loop that is the general copy or the aligned copy of an existing `VersionedLoop` of this rule
is not versioned again.

## How it is proven

`structural+range-facts`: both copies are the same vector loop, so values need no argument; the aligned copy is dominated by one mask-test edge per stream, from which the alignment of every access follows by the step condition. No solver, no timeout path.

## When it must not fire

- Testing the array base while the loop starts at index 1: base 0x1000 passes the test, the first vector access is at 0x1001.
- A `u32` loop with `VF = 2` (8 bytes per step) and `A = 16`: the first access is aligned, the second is at offset 8.
- Testing `(a | b | c) & 15` in one branch and attaching the fact by hand: nothing in the IR proves it, and a later pass that duplicates or moves the test invalidates the attached claim silently.
- Only the stored stream tested: the loads of `a` at 0x1004 run through aligned instructions and fault.
- A `u16` array at an odd address: no peel can align it; here the test simply fails and the general copy runs — which is why this rule exists beside O0401.

## Switching it off

`--disable-opt O0407`; active at `--opt balanced` and `speed`, off at `--opt none`, `size` and `memory` (a second copy of the vector loop).

## Targets

only machine-code and assembly backends, where aligned and unaligned vector accesses are different instructions or have different costs. Managed targets and vector APIs take unaligned data and have no stable addresses; their profiles carry no `alignedPerBlock` and the rule does not fire. For C, C++ and Rust output the single loop is kept: the target compiler versions for alignment itself.

## Interactions with other passes

runs in phase `optimize` after [#510](https://github.com/Hawkynt/Polypiler/issues/510), `OPT-VEC-ALIGNPEEL` and `OPT-VEC-ALIASVERSION` (order O0020, O0401, O0405, O0407) and after [#503](https://github.com/Hawkynt/Polypiler/issues/503) and [#500](https://github.com/Hawkynt/Polypiler/issues/500), because the address exposure makes the regions' identity observable; [#499](https://github.com/Hawkynt/Polypiler/issues/499) reads the facts in the aligned copy.
