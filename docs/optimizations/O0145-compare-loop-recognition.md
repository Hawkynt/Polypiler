# O0145 — Recognize early-exit comparison loops as Memory.Equal or Memory.Compare

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#561](https://github.com/Hawkynt/Polypiler/issues/561). Once implemented it is switched off with `--disable-opt O0145`.

## What it does

Buffers are compared element by element with an early exit: Cipher's `CompareArrays` returns `false` at
the first difference. That is `memcmp`/`SequenceEqual`, and a library compares a word or a vector at a
time. Two things make the recognition delicate. An early-exit loop reads only up to the first
difference, so the single operation may touch memory the loop never reached. And Cipher's
`SecureCompare`/`ConstantTimeCompare` deliberately have *no* early exit: PRD §19.3 forbids introducing
secret-dependent branches, so those must never be merged into the early-exit operation.

## Example

### Before

```
loop:
  %i = Phi.u32 [0, entry], [%i2, latch]
  %c = ICmp.ult.u32 %i, %n
  CondBr %c, body, equal
body:
  %pa = ElementAddr %a, %i
  %pb = ElementAddr %b, %i
  %x = Load.u8 %pa
  %y = Load.u8 %pb
  %ne = ICmp.ne.u8 %x, %y
  CondBr %ne, differ, latch
latch:
  %i2 = Add.u32 %i, 1
  Br loop
equal:
  Ret true
differ:
  Ret false
```

### After

```
%r = Memory.Equal.u8 %a, %b, %n
Ret %r
```

## When it applies

(1) natural loop with exactly two exits — the bound exit and the mismatch
exit — unit step from 0, loop-invariant bound, body free of stores and calls; (2) both loads have the
same integer element type and addresses `base + i`; (3) the results on the two exits are those of the
matched form (`true`/`false`; 0 and ±1 by a comparison of the mismatching pair; the index); for the
three-way form the comparison's signedness becomes the operation's element signedness; (4) read
extent: the loop reads only up to and including the first mismatch; the operation is emitted with
`[read=prefix]`, which promises exactly that, unless range facts prove both spans readable for all `n`
elements — only then with `[read=full]`; (5) both spans are ordinary, non-volatile memory, so the
number of reads is unobservable; (6) the loop is not an accumulating form: there is a data-dependent
exit in the original, so no secret-dependent branch is *introduced*.

## How it is proven

`loop-inductive` (invariant: on reaching iteration `i` the first `i` elements are
pairwise equal) discharged by SMT for one symbolic iteration and both exits; bounds and effect
preconditions as fact obligations; bounded `exhaustive` interpretation for `n` ∈ 0..3 over all 8-bit
contents. A failed, unknown or timed-out obligation (budget 5 s each) leaves the IR unchanged and
reports `not applied: proof timeout` or the failing precondition.

## When it must not fire

- Reading past a short buffer: `a` has 2 elements, `b` has 5, `n = 5`, and `a[0] ≠ b[0]`. The loop
  returns `false` after one read; an operation that reads all five elements of `a` faults or traps.
  `[read=full]` without the readability proof is that operation; `[read=prefix]` is not.
- Signed versus unsigned order: elements `0x80` and `0x01`. With `ICmp.slt` the loop returns −1 (−128
  < 1); `memcmp` order returns +1. The operation must carry the element signedness.
- Constant-time form: `SecureCompare` ORs all differences and has no early exit; rewriting it to
  `Memory.Equal` — which a backend lowers to an early-exit `memcmp` — introduces a timing leak (PRD
  §19.3).
- Volatile buffer: each element read is observable; the loop performs a data-dependent number of them.
- Floating-point elements: `NaN != NaN` makes two bit-identical buffers unequal for the loop.

## Switching it off

`--disable-opt O0145`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` after loop canonicalization; never runs on functions whose
security contract marks them constant-time — and independently refuses the accumulating form; enables
word-wide comparison.
