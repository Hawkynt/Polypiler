# O0030 — Narrow f64 values proven integral to integer types

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#264](https://github.com/Hawkynt/Polypiler/issues/264). Once implemented it is switched off with `--disable-opt O0030`.

## What it does

JavaScript numerics arrive as doubles. [#255](https://github.com/Hawkynt/Polypiler/issues/255) types single operations whose operands are proven
integers, but a loop-carried accumulator — `a = (a + data[i]) % 65521` in Adler-32 — is one web of `f64`
values joined by a phi: unless the whole web changes type together it stays floating point, and so does
everything computed from it. The same happens to C# or Python code that keeps counters in `double`, and to
typed IL that reaches the frontend already as `f64`. An IEEE double represents every integer of magnitude
below 2^53 exactly, so within that range integer arithmetic is the same arithmetic — if the range is
proven. PRD §4.4 (semantic type vs execution width), §4.8 (operation/type narrowing), §8 P2.

## Example

### Before

```
// facts: %byte : u8; %a in [0, 65520] integral (loop invariant proven by range analysis)
head:
  %a  = Phi.f64 [pre: 1.0], [body: %a2]
  ...
body:
  %bf = IntToFloat.f64 %byte
  %t  = Add.f64 %a, %bf
  %a2 = FRem.f64 %t, 65521.0
  Br head
exit:
  %r  = FloatToInt.i32[wrap] %a
```

### After

```
head:
  %a  = Phi.u32 [pre: 1], [body: %a2]
  ...
body:
  %bz = ZExt.u32 %byte
  %t  = Add.u32[wrap] %a, %bz                  // range [0, 65775]: no wrap possible
  %a2 = URem.u32 %t, 65521
  Br head
exit:
  %r  = BitCast.i32 %a                         // value below 2^31: same number
```

## When it applies

A web is narrowed as a whole or not at all. Conditions, with `p` = 53 for `f64`, 24 for `f32`:
(1) **Integrality** — every definition in the web is an integer-valued literal, an `IntToFloat` of an
integer, a `Floor`/`Trunc`/`Round` result, or one of the listed operations on web members; the float-domain
fact from [#88](https://github.com/Hawkynt/Polypiler/issues/88) must say *integral*, `Unknown` blocks the web. (2) **Range** — [#94](https://github.com/Hawkynt/Polypiler/issues/94) proves
for every member an interval inside `[−(2^p − 1), 2^p − 1]`, and for every `Add`, `Sub`, `Mul` the exact
mathematical result lies in that interval too, so the float operation rounded nothing. (3) **Target type** —
the union of all member ranges fits one integer type of at most 64 bits; unsigned if all are non-negative.
(4) **No special values** — NaN and ±∞ are excluded by (1) and (2). `FRem` and division require the divisor
range to exclude 0 (a zero divisor yields NaN in floats and a trap in integers). (5) **Negative zero** — a
member that could be `−0.0` (the product of a negative value and zero, a negated zero, `FRem` of a negative
dividend with zero result) blocks the web if any user can distinguish it: division by the value,
`CopySign`, `FloatBits`, formatting, or an escape as `f64`. Users that are comparisons, additions,
`FloatToInt`, or other web members cannot distinguish it. (6) **Division** — `Div` is not a web operation;
only `Floor(Div a, b)`/`Trunc(Div a, b)` with integral operands map to floor/truncating integer division.
(7) **Boundaries** — a member used outside the web as `f64` gets `IntToFloat` (exact by (2)); a member
stored to memory or passed to a call as `f64` keeps that type at the boundary — the public signature never
changes. (8) `FloatToInt.<t>[wrap]` of a member becomes the integer wrap conversion to `<t>`; `[sat]` and
`[trap]` policies become the corresponding integer range conversion. (9) [#155](https://github.com/Hawkynt/Polypiler/issues/155) prefers the integer
web including boundary conversions.

## How it is proven

`range-facts+smt-bitvector`: the per-operation lemma is proven once by [#71](https://github.com/Hawkynt/Polypiler/issues/71) for `f32` (10 s budget) and checked for `f64` against exact rational arithmetic in the test; each web then needs only its range facts. If the lemma proof times out the pass registers no rule (`not applied: proof timeout`).

## When it must not fire

- `9007199254740992.0 + 1.0` is `9007199254740992.0` in `f64`; as integers it is 9007199254740993. A range that reaches 2^53 must block the web.
- `94906267.0 × 94906267.0` rounds to 9007199515875288; the integer product is 9007199515875289.
- `7.0 / 2.0` is 3.5: plain `Div` is never an integer operation.
- `−5.0 × 0.0` is `−0.0`; `1.0 / (−0.0)` is −∞ while the integer 0 gives +∞ after conversion (or a trap as integer division).
- `FRem.f64 %x, 0.0` is NaN and the loop continues; `URem %x, 0` traps.
- A web member returned from an exported function as `f64`: the signature stays `f64`, only the internal computation narrows.

## Switching it off

`--disable-opt O0030`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `specialize` after [#253](https://github.com/Hawkynt/Polypiler/issues/253) and the language specializations have produced typed `f64` code and after range analysis; before [#161](https://github.com/Hawkynt/Polypiler/issues/161), [#165](https://github.com/Hawkynt/Polypiler/issues/165), [#502](https://github.com/Hawkynt/Polypiler/issues/502) and the recognition re-sweep it requests (`RequestSweep(Recognize)` of [#157](https://github.com/Hawkynt/Polypiler/issues/157)) so integer idioms such as modular reductions and rotates are recognized.
