# O0401 — Peel scalar iterations until the stored address of a vector loop is aligned

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0401`.

## What it does

A logical vector access has no alignment requirement ([#381](https://github.com/Hawkynt/Polypiler/issues/381)), and that is the right meaning — but on
machine-code targets an access that straddles a cache line or a page costs extra, and the aligned
instruction forms fault on misaligned data. [#510](https://github.com/Hawkynt/Polypiler/issues/510) starts its vector loop at element 0 wherever the
array happens to lie. Running a few scalar iterations first, until the address reaches a vector boundary,
makes alignment a proven fact of the main loop instead of an assumption. PRD §4.8 (SIMD vectorization), §8 P3.

## Example

### Before

```
vloop:                                        // i = 0, 16, .. while i + 16 <= n          (output of O0020)
  %vx = VecLoad.vec<u8, 16>[inbounds #1] %a, %i
  %vz = Xor.vec<u8, 16> %vx, %vk
  VecStore.vec<u8, 16>[inbounds #2] %c, %i, %vz
tail:                                         // scalar loop for the remaining n mod 16 elements
```

### After

```
pre:
  %c0   = ElemAddr.ptr<u8>[inbounds #2] %c, 0
  %adr  = PtrToInt.uptr[expose] %c0
  %mis  = And.uptr %adr, 15
  %gap  = And.uptr (Sub.uptr[wrap] 16, %mis), 15   // bytes to the next multiple of 16: 0 .. 15
  %pe   = LShr.uptr[strict] %gap, 0                // elements = bytes >> log2(element size)
  %peel = UMin.uptr %pe, %n
peel:                                         // the unchanged scalar body for i = 0 .. %peel - 1
test:
  %cp   = ElemAddr.ptr<u8>[inbounds #2] %c, %peel
  %ok   = ICmp.eq.uptr (And.uptr (PtrToInt.uptr[expose] %cp), 15), 0
  Br %ok, vloop, tail
vloop:                                        // i = %peel, %peel + 16, .. while i + 16 <= n; %c + i is a multiple of 16
tail:                                         // the unchanged scalar body for the remaining elements
```

## When it applies

With alignment `A` (a power of two, the byte width of the vector or a smaller value the profile prices) and
element size `s` of the chosen stream: (1) the function contains the loop triple of [#510](https://github.com/Hawkynt/Polypiler/issues/510) — one
vector loop and the original scalar loop as its remainder on the same induction variable — and the peel
loop is a third copy of that scalar body, so no value or effect argument beyond O0020's is needed.
(2) `s` divides `A` and the base address of the stream is a multiple of `s`: `IsAligned(base, s) = True`
from [#83](https://github.com/Hawkynt/Polypiler/issues/83), or `s = 1`. Without that fact no number of whole elements reaches a boundary.
(3) Exactly one stream is aligned: the stored one if the loop stores, else the first loaded one; the other
streams keep their unaligned accesses. (4) The peel count is clamped to the trip count
(`UMin(pe, n)`) and the vector loop is entered only through the test edge; when the loop is shorter than
the peel count the test fails and the tail runs zero iterations. (5) The profile can take the address of
the region as an integer: the region is `pinned` or of lifetime class `stack`, `heap` or `static` on a
profile that has addresses ([#123](https://github.com/Hawkynt/Polypiler/issues/123)); a profile without stable addresses declares no
`alignedPerBlock` and the rule does not fire. (6) [#155](https://github.com/Hawkynt/Polypiler/issues/155) prefers
`BlockedCost` with `alignedPerBlock` plus the expected peel (`(A / s − 1) / 2` scalar iterations) over the
unpeeled loop for the proven or assumed trip count ([#350](https://github.com/Hawkynt/Polypiler/issues/350), [#94](https://github.com/Hawkynt/Polypiler/issues/94)).

## How it is proven

`structural+exhaustive-16bit+range-facts`: peel, vector and tail loops partition `0 … n−1` in order and each runs a body O0020 already proved equivalent; `(adr + ((A − (adr & (A−1))) & (A−1))) & (A−1) = 0` is checked by [#70](https://github.com/Hawkynt/Polypiler/issues/70) for all 16-bit `adr` and `A` ∈ {2, 4, 8, 16, 32, 64}; alignment inside the loop follows from the test edge and the step `A` ([#84](https://github.com/Hawkynt/Polypiler/issues/84)). No solver, no timeout path.

## When it must not fire

- An aligned vector store emitted on the assumption that arrays are aligned: with the array at 0x1004 the first vector access is at 0x1004 and the aligned x86 form faults.
- A `u16` array at the odd address 0x1001: `gap = 15` bytes is 7.5 elements; after 7 elements the address is 0x100F and after 8 it is 0x1011 — no whole element count aligns it.
- `n = 5` with a peel count of 12: an unclamped peel loop runs 12 iterations and writes 7 elements past the end.
- Two streams with different residues (`a` at 0x1004, `c` at 0x2008): after aligning `c` (peel 8) the loads of `a` start at 0x100C; treating them as aligned is wrong.
- A managed array whose storage the collector may move: the address read before the loop says nothing about the address during it.

## Switching it off

`--disable-opt O0401`; active at `--opt balanced` and `speed`, off at `--opt none`, `size` and `memory`.

## Targets

only machine-code and assembly backends pay: there the aligned instruction forms are cheaper and accesses split across cache lines or pages disappear. Managed targets (.NET, JVM, JavaScript, Python) expose no stable address and their vector APIs take unaligned data; their profiles carry no `alignedPerBlock`, so the generated source keeps the two-loop shape of O0020. GCC and LLVM peel for alignment on their own, so C, C++ and Rust profiles normally omit the key as well.

## Interactions with other passes

runs in phase `optimize` directly after [#510](https://github.com/Hawkynt/Polypiler/issues/510) and after [#503](https://github.com/Hawkynt/Polypiler/issues/503) and [#500](https://github.com/Hawkynt/Polypiler/issues/500) (the `PtrToInt[expose]` makes the region's identity observable, which would block them); [#499](https://github.com/Hawkynt/Polypiler/issues/499)'s `native` alternative reads the alignment fact; `OPT-VEC-ALIGNVERSION` handles loops this rule refuses for lack of an element-aligned base.
