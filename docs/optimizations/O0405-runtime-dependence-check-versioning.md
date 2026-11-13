# O0405 — Guard a vector loop by a run-time disjointness test of its accessed ranges, keeping the scalar loop as fallback

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0405`.

## What it does

[#510](https://github.com/Hawkynt/Polypiler/issues/510) vectorizes `c[i] = a[i] ^ b[i]` only when [#82](https://github.com/Hawkynt/Polypiler/issues/82) proves the arrays distinct, and for
slices that arrive as parameters nothing can prove that: the caller may pass the same buffer twice. Most
calls do not. Testing the accessed ranges once before the loop and keeping the unchanged scalar loop for the
overlapping case recovers the vector path for exactly the calls where it is legal — [#82](https://github.com/Hawkynt/Polypiler/issues/82) names
this as an optimization that belongs on [#157](https://github.com/Hawkynt/Polypiler/issues/157). PRD §4.8 (SIMD vectorization), §8 P3.

## Example

### Before

```
func @xor3(%a: roslice<u8>, %b: roslice<u8>, %c: slice<u8>, %n: uptr) -> void
loop:                                         // i = 0 .. n-1; O0020: "arrays %a and %c may alias"
  %x = Load.u8[inbounds #1] %a, %i
  %y = Load.u8[inbounds #2] %b, %i
  %z = Xor.u8 %x, %y
  Store.u8[inbounds #3] %c, %i, %z
```

### After

```
guard:
  %d1 = RangesDisjoint.bool %c, %n, %a, %n
  %d2 = RangesDisjoint.bool %c, %n, %b, %n
  %ok = And.bool %d1, %d2
  Br %ok, fast, slow
fast:                                         // clone; accesses carry alias=g1 (%c), alias=g2 (%a), alias=g3 (%b), evidence checked
  // O0020 vectorizes this clone on the next iteration of the phase:
  //   vloop: VecLoad.vec<u8, 16> / Xor.vec<u8, 16> / VecStore.vec<u8, 16>, then its scalar tail
slow:                                         // the original loop, instruction for instruction
```

## When it applies

(1) The loop meets every condition of [#510](https://github.com/Hawkynt/Polypiler/issues/510) except its condition 4, and the only failing pairs are a
store and another access through **different base values** for which [#82](https://github.com/Hawkynt/Polypiler/issues/82) answers `MayAlias`;
a pair on the same base with a non-zero distance is a real dependence and blocks the rule. (2) For every
base the accessed element range over the whole loop is computable before the loop: index
`AddRec(lo, 1)` without wrap and a trip count `Exact` or `Symbolic` ([#350](https://github.com/Hawkynt/Polypiler/issues/350)); the range is
`[base + lo, base + lo + trip)`. (3) The guard tests `RangesDisjoint` for every (stored range, other
range) pair that is not statically `NoAlias`; at most 8 tests. (4) `RangesDisjoint %p, %np, %q, %nq` is
true iff the byte ranges `[p, p + np × size)` and `[q, q + nq × size)` have no byte in common, computed on
mathematical addresses; an empty range is disjoint from everything. It is pure, reads no memory and
cannot trap. Decomposition for profiles with addresses: with `P`, `Q` the integer addresses,
`(Q ≥ P ∧ Q − P ≥ np × size) ∨ (P > Q ∧ P − Q ≥ nq × size)` in unsigned arithmetic — never by forming an
end address. For profiles with region identity only: regions differ, or the same test on element indices
inside the common region. (5) The guard-proven facts hold only at program points dominated by the
guard's true edge, and only between classes of the same guard; the fallback carries none. (6) The fast
clone's bases must not be redefined between guard and loop. (7) [#155](https://github.com/Hawkynt/Polypiler/issues/155) accepts the clone: expected
gain of the vector form for the proven or assumed trip count against the added size; under `--opt size`
and `memory` the rule is off. (8) No loop is cloned twice: a fallback of an existing `VersionedLoop`
is never a candidate (O0020 still refuses it, and it would otherwise be cloned on every iteration of the
phase), and when the refused loop is the fast clone of a guard created by `OPT-LOOP-VERSION` the range
tests are and-ed into that guard and the slots attached to that clone.

## How it is proven

`structural+alias-facts`: when the guard is true the clone's stored range shares no byte with any other accessed range, which is the `NoAlias` premise O0020 requires; when it is false the original loop runs. The predicate's decomposition is checked against a brute-force intersection for all 8-bit addresses and lengths. No solver, no timeout path.

## When it must not fire

- `inc(a, a + 1, n)` with the body `c[i] = a[i] + 1` — `c` is `a` shifted by one element: on the buffer `[1, 0, 0, 0, 0]` with `n = 4` the sequential loop leaves `[1, 2, 3, 4, 5]`; a vector loop that loads `a[0..3]` before storing leaves `[1, 2, 1, 1, 1]`.
- A buffer that ends at the top of the address space: `P = 0xFFF0`, 16 bytes, `Q = 0xFFF8`. Forming the end address gives `0x10000`, truncated to 0, and `end ≤ Q` wrongly reports disjoint.
- Two views of one buffer with different element types (`u8` at offset 2 and `u32` at offset 0): the test must compare byte ranges, not element indices.
- Using the fact outside the guard: a later loop over the same parameters is not covered by this guard.
- `a[i + 1] = a[i]` inside one array: versioning cannot help; both versions would be the same loop.
- A loop that is also blocked by a possible trap: making the alias question disappear does not make it vectorizable; cloning it would only double the code.

## Switching it off

`--disable-opt O0405`; active at `--opt balanced` and `speed`, off at `--opt none`, `size` and `memory` (the clone doubles the loop).

## Targets

every target on which O0020's vector form pays, i.e. where vectors are native or reachable through a vector API, and the source language cannot state non-aliasing. It matters most for library functions with slice or pointer parameters. Output for a target compiler that versions loops itself (GCC and LLVM do, for C, C++ and Rust) keeps the single scalar loop — the cost profile prices the clone above it — so the generated source does not carry two copies of a loop its compiler would duplicate anyway.

## Interactions with other passes

runs in phase `optimize` after [#510](https://github.com/Hawkynt/Polypiler/issues/510) has refused the loop; because it reports `Changed`, the phase iterates and O0020 vectorizes the clone on the next sweep. `OPT-LOOP-VERSION` runs before [#510](https://github.com/Hawkynt/Polypiler/issues/510) and may already have put a guard on the loop; `OPT-VEC-ALIGNVERSION` builds its copies with the same `LoopCloner`. [#508](https://github.com/Hawkynt/Polypiler/issues/508) may hoist out of the clone because the preheader is still dominated by the guard.
