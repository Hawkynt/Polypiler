# O0101 — Recognize population count from bit-counting loops

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#278](https://github.com/Hawkynt/Polypiler/issues/278). Once implemented it is switched off with `--disable-opt O0101`.

## What it does

Counting set bits is written as a loop in every language without a popcount operator: Cipher's
`PopCount` shifts and adds the low bit, `PopCountFast` clears the lowest set bit per iteration
(Kernighan), and C-style code tests each of the `w` bit positions. As a loop it costs up to `w`
iterations where C# has `BitOperations.PopCount`, Rust `count_ones` and x86 `POPCNT`. PRD §4.3
(`PopCount` may be recovered from scalar loops), §4.5, §8 P2 ("popcount").

## Example

### Before

```
entry:
  Br loop
loop:
  %v = Phi.u32 [%x, entry], [%v2, body]
  %c = Phi.u32 [0, entry], [%c2, body]
  %z = ICmp.eq.u32 %v, 0
  CondBr %z, exit, body
body:
  %m = Sub.u32 %v, 1
  %v2 = And.u32 %v, %m
  %c2 = Add.u32 %c, 1
  Br loop
exit:
  Ret %c
```

### After

```
%c = PopCount.u32 %x
Ret %c
```

## When it applies

the loop is a natural loop with one exit and two loop-carried values `v`
and `c`; `v` starts at `x` of type `u<w>` (or `i<w>` when every right shift is logical); the body is
one of the three forms and contains no load, store, call or trap; neither `v` nor any body value is
used outside the loop except the exit value of `c`; the fixed-trip form runs exactly `w` iterations
with shift amounts `0..w-1`; the counter has at least 8 bits, so `c0 + w` wraps exactly as the loop's
own additions do. The result is `Add(c0, ZExt-or-Trunc(PopCount.u<w> x))` in the counter's type; the
`Add` is omitted when `c0` is the constant 0.

## How it is proven

`exhaustive-8bit` (all 256 values × three forms, loop interpreted to termination)
plus `smt-bitvector` at 16, 32 and 64 bit on the loop unrolled `w` times — each form terminates within
`w` iterations, which is itself an obligation (Kernighan clears one set bit per iteration; the shift
form shifts in a zero). A failed, unknown or timed-out obligation (budget 5 s each) leaves the IR
unchanged and reports `not applied: proof timeout` or the failing precondition.

## When it must not fire

- Arithmetic shift in the shift form: with `i32` and `x = 0x80000000`, `AShr` keeps `v` negative, `v`
  reaches `0xFFFFFFFF` and the loop never ends — `PopCount` would return 1.
- Fixed-trip form with 16 iterations over `u32`: `x = 0x00010000` gives 0, `PopCount.u32` gives 1.
- A body that also stores (`Store.u8 %p, %c2`): the per-iteration writes are observable.
- `v` read after the loop: its exit value 0 must stay available, so the loop-carried value escapes.

## Switching it off

`--disable-opt O0101`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` after loop canonicalization and constant folding of
[#157](https://github.com/Hawkynt/Polypiler/issues/157); enables [#573](https://github.com/Hawkynt/Polypiler/issues/573) (`PopCount & 1`) and native population-count selection in
the backends.
