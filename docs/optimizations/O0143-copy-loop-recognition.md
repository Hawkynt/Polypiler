# O0143 — Recognize element copy loops as Memory.Copy, Memory.Move or Memory.CopyForward

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#273](https://github.com/Hawkynt/Polypiler/issues/273). Once implemented it is switched off with `--disable-opt O0143`.

## What it does

`for (i = 0; i < n; i++) dst[d + i] = src[s + i]` is `memcpy` written out — Cipher's `CopyBytes` and
`CopyArray` are exactly that. As a loop it costs a bounds check and a branch per element; as
`Memory.Copy` it becomes `Array.Copy`, `Span.CopyTo`, `copy_from_slice`, `REP MOVSB` or a tight 6502
loop chosen by the backend. Whether it is a copy, a move or neither depends on overlap and direction:
PRD §17.3 forbids turning an overlap-safe move into `memcpy`, and a forward loop over a destination that
lies ahead of its source is neither — it is the replicating `Memory.CopyForward`. PRD §4.5, §8 P2
("array copy").

## Example

### Before

```
loop:
  %i = Phi.u32 [0, entry], [%i2, body]
  %c = ICmp.ult.u32 %i, %n
  CondBr %c, body, exit
body:
  %ps = ElementAddr %src, %i
  %pd = ElementAddr %dst, %i
  %v = Load.u8 %ps
  Store.u8 %pd, %v
  %i2 = Add.u32 %i, 1
  Br loop
```

### After

```
Memory.Copy.u8 %dst, %src, %n
```

## When it applies

(1) natural loop, one exit, unit step, loop-invariant bound evaluated
once, no other effect in the body, no loop value used afterwards, the induction variable cannot wrap
before the bound; (2) load and store have the same element type and no conversion between them; (3)
addresses are affine with coefficient 1 in the induction variable; (4) overlap classification by
[#82](https://github.com/Hawkynt/Polypiler/issues/82) over the full ranges: `NoAlias` → `Memory.Copy`; same allocation with a proven offset
relation → `Memory.Move` if the loop is ascending and `dst ≤ src`, or descending and `dst ≥ src`; any
other ascending loop (`dst > src` overlapping, or `MayAlias` without an offset relation) →
`Memory.CopyForward`, whose definition is the ascending loop itself; any other descending loop →
refused; (5) every access of the loop is proven in bounds for the whole range `[0, n)` before the loop
starts (range facts relating `n` to the span lengths), or the accesses carry no bounds trap in their
source profile; otherwise the original performs the first `k` iterations and then traps with a
partially written buffer, which the single operation cannot reproduce; (6) both ranges are ordinary,
non-volatile memory.

## How it is proven

`loop-inductive` (invariant: after `i` iterations the first `i` destination elements
— the last `i` for a descending loop — hold the original source elements, everything else unchanged),
discharged by SMT for one symbolic iteration under the overlap classification; alias, bounds and
effect preconditions as fact obligations; bounded `exhaustive` interpretation for `n` ∈ 0..4 over all
placements of two ranges in a 6-element buffer. A failed, unknown or timed-out obligation (budget 5 s
each) leaves the IR unchanged and reports `not applied: proof timeout` or the failing precondition.

## When it must not fire

- Ascending loop, destination one ahead: buffer `01 02 03 04 05`, `dst = buf + 1`, `src = buf`, `n =
  4` yields `01 01 01 01 01`. `Memory.Move` yields `01 01 02 03 04`; `Memory.Copy` traps on the
  overlap. Neither is the loop; `Memory.CopyForward` is.
- Unknown overlap rewritten as `Memory.Copy`: correct for disjoint arguments, wrong for a caller that
  passes overlapping ones (the `memcpy`-for-`memmove` defect of PRD §17.3).
- Descending loop with the destination below an overlapping source: buffer `01 02 03 04 05`, `dst =
  buf`, `src = buf + 1`, `n = 4`, index running from 3 down to 0, yields `05 05 05 05 05`; no
  operation of [#189](https://github.com/Hawkynt/Polypiler/issues/189) replicates backward, so the loop stays.
- Trap after partial work: copying 8 elements into a 4-element array with trapping bounds leaves four
  copied elements and raises.
- Conversion in the body: `u8` loaded, `u32` stored — a widening map, not a copy.
- Volatile source: each element read is an observable access in order.

## Switching it off

`--disable-opt O0143`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` after loop canonicalization and alias analysis, before
[#281](https://github.com/Hawkynt/Polypiler/issues/281) (O0150) by rule order; enables copy elision and library-call selection.
