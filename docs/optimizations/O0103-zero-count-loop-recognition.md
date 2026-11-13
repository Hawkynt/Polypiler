# O0103 — Recognize leading and trailing zero counts from bit-scanning loops

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#584](https://github.com/Hawkynt/Polypiler/issues/584). Once implemented it is switched off with `--disable-opt O0103`.

## What it does

Finding the lowest or highest set bit is written as a loop that shifts until a bit appears. Range
coders, Huffman builders and normalization code do it per symbol, and the loop hides
`TrailingZeroCount`/`LeadingZeroCount` — `BitOperations` in C#, `trailing_zeros` in Rust,
`TZCNT`/`LZCNT`/`CLZ` in hardware. The loop also has a trap the intrinsic lacks: for an input of zero
the unguarded form never ends. PRD §4.5, §19.2 (algorithm level).

## Example

### Before

```
entry:
  Br loop
loop:
  %v = Phi.u32 [%x, entry], [%v2, body]
  %c = Phi.u32 [0, entry], [%c2, body]
  %b = And.u32 %v, 1
  %s = ICmp.ne.u32 %b, 0
  CondBr %s, exit, body
body:
  %v2 = LShr.u32[strict] %v, 1
  %c2 = Add.u32 %c, 1
  Br loop
exit:
  Ret %c
```

### After

```
%c = TrailingZeroCount.u32 %x
Ret %c      ; only with the fact x ≠ 0
```

## When it applies

natural loop, one exit, pure body, no escape of loop values other than the
count. Guarded forms: the bound is exactly `w`; the result is the intrinsic for every `x`. Unguarded
trailing-zero form: a range or known-bits fact `x ≠ 0` at the loop entry is required, because for `x =
0` the loop does not terminate. Bit-length form: the shift is logical and the result is `Sub(w,
LeadingZeroCount x)` in the counter's type (which terminates for every `x`). A non-zero initial
counter is added as in [#278](https://github.com/Hawkynt/Polypiler/issues/278).

## How it is proven

`exhaustive-8bit` for each form (unguarded form over the 255 non-zero values) plus
`smt-bitvector` at 16, 32 and 64 bit on the loop unrolled `w` times; the fact `x ≠ 0` is a `RangeFact`
obligation answered by [#94](https://github.com/Hawkynt/Polypiler/issues/94). A failed, unknown or timed-out obligation (budget 5 s each)
leaves the IR unchanged and reports `not applied: proof timeout` or the failing precondition.

## When it must not fire

- Unguarded trailing-zero loop with `x = 0`: the loop never terminates; `TrailingZeroCount.u32 0` is
  32.
- Bit-length loop with `AShr` on `i32`, `x = 0x80000000`: `v` stays negative, the loop never
  terminates.
- Guarded loop with bound 16 over `u32`, `x = 0x00010000`: the loop gives 16, `TrailingZeroCount`
  gives 16 — but `x = 0x01000000` gives 16 against 24.
- Scan from bit 1 instead of bit 0 (`c` starts at 1 and tests `x >>> c`): `x = 1` gives 32, not 0.

## Switching it off

`--disable-opt O0103`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` after range propagation, which supplies `x ≠ 0` from a
dominating branch; enables native bit-scan selection.
