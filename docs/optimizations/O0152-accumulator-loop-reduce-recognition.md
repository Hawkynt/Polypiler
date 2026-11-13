# O0152 — Recognize accumulator loops as Reduce with a recorded associativity

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#282](https://github.com/Hawkynt/Polypiler/issues/282). Once implemented it is switched off with `--disable-opt O0152`.

## What it does

Checksums, norms, counts, minima and maxima fold a sequence into one value: `acc = init; for (…) acc =
op(acc, f(src[i]))`. Cipher's XOR and LRC checksums, CompressionWorkbench's Adler-32 and the energy sum
in its RA-144 codec are all this loop. `Reduce` lets a target print `Sum`/`fold`/`reduce` and lets the
optimizer split the fold across vector lanes or threads — but only when the operation is associative
under the source's exact arithmetic: PRD §4.8 allows parallel reduction "when associative", and wrapping
integer addition is, while floating-point and checked addition are not. PRD §4.3, §4.5, §8 P2. Sequence
operations are written exactly as [#195](https://github.com/Hawkynt/Polypiler/issues/195), [#194](https://github.com/Hawkynt/Polypiler/issues/194) and [#197](https://github.com/Hawkynt/Polypiler/issues/197) define them
on top of [#201](https://github.com/Hawkynt/Polypiler/issues/201); this issue adds no notation of its own.

## Example

### Before

```
loop:
  %i = Phi.u32 [0, entry], [%i2, body]
  %acc = Phi.u8 [%init, entry], [%acc2, body]
  %c = ICmp.ult.u32 %i, %n
  CondBr %c, body, exit
body:
  %p = ElementAddr %src, %i
  %x = Load.u8 %p
  %acc2 = Xor.u8 %acc, %x
  %i2 = Add.u32 %i, 1
  Br loop
exit:
  Ret %acc
```

### After

```
%acc = Reduce.u8[order=any, associative=true] %src, %n, %init, λ(%a, %x): Xor.u8 %a, %x
Ret %acc
```

## When it applies

(1) natural loop, one exit, unit step from 0, loop-invariant bound, no
wrap; (2) exactly one loop-carried value besides the index; its update uses the previous value and
values derived only from the elements at `i`, invariants and `i`; (3) the body has no store, no
effectful call, and the accumulator is not used for anything but its own update inside the loop; (4)
the source spans are proven readable for `n` elements or their loads carry no trap; ordinary,
non-volatile memory; (5) `associative` is set only from the table in *Scope*, never inferred from the
operator's name alone — the operation's overflow contract and type decide; (6) `order=sequential`
whenever `associative=false` or the body can trap.

## How it is proven

`loop-inductive` (invariant: after `i` iterations the accumulator equals the left
fold of the first `i` elements from `init`), discharged by SMT for one symbolic iteration; for
`associative=true` an additional `smt-bitvector` obligation proves associativity and commutativity of
the combiner at the accumulator's type (`exhaustive-8bit` at 8 bit); fact obligations for bounds and
effects. A failed, unknown or timed-out obligation (budget 5 s each) leaves the IR unchanged and
reports `not applied: proof timeout` or the failing precondition.

## When it must not fire

- Floating-point sum marked associative: in `f32`, `(16777216 + 1) + (−16777216)` is 0 because
  `16777216 + 1` rounds to `16777216`, while `(16777216 + (−16777216)) + 1` is 1. A lane-split sum
  changes the result.
- Checked integer sum marked associative: `[2147483647, 1, −1]` traps at the second element in order;
  reordered as `(1 + −1) + 2147483647` it does not trap.
- Modular update: `(acc + x) % 65521` is not a fold of an associative operation over the raw elements;
  treating it as `Sum % 65521` is valid only without intermediate wrap, which is a separate proof this
  rule does not attempt.
- A scan: storing `acc` to `dst[i]` each iteration makes every intermediate value observable.
- Coupled accumulators: Adler's `b += a` depends on the other accumulator's running value; `b` is not
  a fold of the elements alone.

## Switching it off

`--disable-opt O0152`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` after [#563](https://github.com/Hawkynt/Polypiler/issues/563), [#564](https://github.com/Hawkynt/Polypiler/issues/564) and
[#281](https://github.com/Hawkynt/Polypiler/issues/281) by rule order; enables [#566](https://github.com/Hawkynt/Polypiler/issues/566), map-reduce fusion and — only for
`associative=true` — vector and parallel reduction.
