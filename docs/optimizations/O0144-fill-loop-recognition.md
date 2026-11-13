# O0144 — Recognize element fill loops as Memory.Fill

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#277](https://github.com/Hawkynt/Polypiler/issues/277). Once implemented it is switched off with `--disable-opt O0144`.

## What it does

`for (i = 0; i < n; i++) a[i] = v` — Cipher's `ClearArray` zeroes key material this way — is `memset`.
`Array.Clear`, `Span.Fill`, `fill`, `REP STOSB` and PowerBASIC's array initializers all need to see one
operation; PB-Compiler already has an optimization that elides zero fills of fresh arrays, which
presupposes the fill being recognizable. PRD §4.5, §8 P2 ("array fill").

## Example

### Before

```
loop:
  %i = Phi.u32 [0, entry], [%i2, body]
  %c = ICmp.ult.u32 %i, %n
  CondBr %c, body, exit
body:
  %pd = ElementAddr %dst, %i
  Store.u8 %pd, %v
  %i2 = Add.u32 %i, 1
  Br loop
```

### After

```
Memory.Fill.u8 %dst, %v, %n
```

## When it applies

(1) natural loop, one exit, unit step, loop-invariant bound evaluated
once, the store is the only effect in the body, no loop value used afterwards, no wrap of the
induction variable; (2) the stored value is loop-invariant: an SSA value defined outside the loop, or
a load inside the loop from a location proven disjoint from the destination range (so no iteration's
store can change it); (3) the address is `dst + i` with coefficient 1; (4) every access of the loop is
proven in bounds for the whole range `[0, n)` before the loop starts (range facts relating `n` to the
span lengths), or the accesses carry no bounds trap in their source profile; otherwise the original
performs the first `k` iterations and then traps with a partially written buffer, which the single
operation cannot reproduce; (5) the destination is ordinary, non-volatile memory.

## How it is proven

`loop-inductive` (invariant: after `i` iterations the first `i` elements equal `v`,
everything else unchanged), discharged by SMT for one symbolic iteration; bounds, alias and effect
preconditions as fact obligations; bounded `exhaustive` interpretation for `n` ∈ 0..4 and all 8-bit
`v`. A failed, unknown or timed-out obligation (budget 5 s each) leaves the IR unchanged and reports
`not applied: proof timeout` or the failing precondition.

## When it must not fire

- Ramp: `a[i] = (byte)i` stores 0, 1, 2, … — the stored value is not invariant.
- Stride 2: `a[2*i] = v` leaves every other element untouched.
- A value reloaded from memory that the fill itself overwrites with a different width: `v = *(u32*)q`
  reloaded per iteration while byte stores run through `q` — after the first overlapping store the
  loaded value changes.
- Trap after partial work: filling 8 elements of a 4-element array with trapping bounds.
- Volatile destination: `n` writes to one device register are `n` bus cycles, and to `n` registers
  they are ordered.

## Switching it off

`--disable-opt O0144`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` after loop canonicalization and loop-invariant code motion
(so an invariant load is already outside the loop where possible), before [#281](https://github.com/Hawkynt/Polypiler/issues/281) (O0150);
enables zero-fill elision and library-call selection.
