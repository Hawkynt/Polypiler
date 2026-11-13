# O0303 — Carry constant-coefficient affine functions of a loop counter as their own recurrences

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0303`.

## What it does

`j = 6 * i + 3` inside a loop is a multiplication per iteration that an addition can replace: when `i` grows
by 1, `j` grows by 6. [#165](https://github.com/Hawkynt/Polypiler/issues/165) reduces single multiplications and declares the loop case out of scope
("induction-variable strength reduction in loops → not planned yet"); this issue plans it for the
constant-coefficient case, which is exact in wrapping arithmetic without any range proof. PB-Compiler groups
it with loop rotation and loop fusion in one document; those are separate rewrites and live in
`OPT-LOOP-ROTATE` and `OPT-LOOP-FUSE`. PRD §4.8 (strength reduction), §23.5.

## Example

### Before

```
head:
  %i = Phi.u32 [pre: 0], [body: %i2]
  %c = ICmp.ult.u32 %i, %n
  Br %c, body, exit
body:
  %m = Mul.u32[wrap] %i, 6
  %j = Add.u32[wrap] %m, 3
  Call.void @use(%j)
  %i2 = Add.u32[wrap] %i, 1
  Br head
```

### After

```
head:
  %i = Phi.u32 [pre: 0], [body: %i2]
  %j = Phi.u32 [pre: 3], [body: %j2]
  %c = ICmp.ult.u32 %i, %n
  Br %c, body, exit
body:
  Call.void @use(%j)
  %j2 = Add.u32[wrap] %j, 6
  %i2 = Add.u32[wrap] %i, 1
  Br head
```

## When it applies

(1) `i` is a header phi with `AddRec(start, step)`, `step` a constant ([#350](https://github.com/Hawkynt/Polypiler/issues/350)); no
no-wrap flag is needed. (2) Every operation between `i` and the derived value is `Add`, `Sub`, `Mul` by a
constant or `Shl` by a constant amount below the width, carries the `[wrap]` contract ([#122](https://github.com/Hawkynt/Polypiler/issues/122)) and
has the counter's type; the derived value is defined in a block the latch is dominated by or dominates —
anywhere in the loop — because the phi form is available from the header on. (3) The tree is at most 16
operations deep. (4) At least one `Mul` or `Shl` becomes dead. (5) [#155](https://github.com/Hawkynt/Polypiler/issues/155) prefers the recurrence; on
an `emits: source` profile ([#262](https://github.com/Hawkynt/Polypiler/issues/262)) the rewrite is offered at `--opt speed` only.

## How it is proven

`exhaustive-8bit+structural`: the affine identity holds in `Z/2^w` for every width (a ring identity, checked exhaustively at 8 bit); the phi satisfies it by induction over the iterations. No solver, no timeout path.

## When it must not fire

- `Mul.u8[sat] %i, 100`: for `i = 3` the product saturates at 255; the recurrence 0, 100, 200, 44 wraps.
- `Mul.i32[trap Overflow] %i, 6`: the source traps when the product overflows; an added `[wrap]` recurrence never traps.
- `Mul.u32[wrap] %i, %i`: not affine — the difference between consecutive values is not constant.
- `UDiv.u32 %i, 2`: 0, 0, 1, 1, 2 is not a recurrence with a constant step.
- `ZeroExtend.u32 (Mul.u8[wrap] %i8, 100)`: the product wraps at 8 bit before it is widened; a 32-bit recurrence 0, 100, 200, 300 is wrong from the fourth value on (44).

## Targets

Machine-code and assembly backends whose profile prices `Mul` above `Add` — all of them, most of
all CPUs without a multiply instruction (6502, Z80, 8080) or with a slow one (8086) — and source targets
with no optimizing compiler behind them (PowerBASIC, tokenized BASIC) at `--opt speed`. Mature source
targets (C, C++, Rust, C#, Java) perform this reduction themselves; there a carried `j += 6` only makes the
output harder to read, so at `balanced`, `size` and `memory` an `emits: source` profile gets the report line
`source target at this level` and no change. [#155](https://github.com/Hawkynt/Polypiler/issues/155) ranks `multiply` (the original operations per
iteration) against `recurrence` (one `Add` per iteration), original first.

## Switching it off

`--disable-opt O0303`; active at `--opt balanced`, `speed`, `size` and `memory` on profiles that are not `emits: source`; at `--opt speed` only on `emits: source` profiles; off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` after [#508](https://github.com/Hawkynt/Polypiler/issues/508) and [#165](https://github.com/Hawkynt/Polypiler/issues/165) (which turns `i * 2` into `i << 1`, a form this pass accepts) and immediately before `OPT-LOOP-IVREDUNDANT`; the dead multiplications are collected by [#156](https://github.com/Hawkynt/Polypiler/issues/156).
