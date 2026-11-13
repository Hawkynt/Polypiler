# O0077 — Lower division, float and bounds semantics that differ on the target

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#1069](https://github.com/Hawkynt/Polypiler/issues/1069). Once implemented it is switched off with `--disable-opt O0077`.

## What it does

[#256](https://github.com/Hawkynt/Polypiler/issues/256) covers operations a target *lacks*. A second group exists on the target but means something
else there: Python's and Ruby's integer `//` floors where the IR's `SDiv` truncates; Python and Perl raise on
a floating-point division by zero where IEEE 754 returns ±∞ or NaN; several dynamic languages have only
binary64, so `f32` arithmetic must be rounded after every step; a float-to-integer conversion that must trap
out of range has no native form almost anywhere; and an array access that the IR requires to fail (`[bounds]`)
silently succeeds in Perl (autovivification) and for negative indices in Python. The Kotlin, Perl and Python
backends each registered private rules for these (`python.sdiv`, `python.fdiv`, `perl.fdiv`, `python.f32`,
`*.f2i-trap`, `perl.bounds`, `python.bounds`). This issue is the shared, proven rule set that replaces them;
it extends the rule index of O0079 and uses the same contribution contract. PRD §17.1, §4.9, §21.3.

## Example

### Before

```
%q = SDiv.i32[wrap] %a, %b                    // target divides by flooring
%v = Load.u8[bounds] %arr, %i                 // target array access never fails
```

### After

```
// O0077.sdiv-trunc, inline form; FloorDiv/FloorRem are the target's native operations
%f = FloorDiv.i32 %a, %b
%m = FloorRem.i32 %a, %b
%z = ICmp.ne.i32 %m, 0
%x = Xor.i32 %a, %b
%s = ICmp.slt.i32 %x, 0                       // operands have different signs
%c = And.bool %z, %s
%q = Add.i32[wrap] %f, (ZExt.i32 %c)          // round the floored quotient toward zero
// O0077.bounds-assert
%n = ArrayLen.uptr %arr
Assert (ICmp.ult.uptr %i, %n) [trap IndexOutOfRange]
%v = Load.u8[unchecked] %arr, %i
```

## When it applies

A rule applies only when the profile key selects it and the operation is not listed `Native` with the IR's semantics.
`sdiv-trunc` — for `b ≠ 0`: `q = floor(a / b) + ((a mod_floor b ≠ 0) and ((a xor b) < 0) ? 1 : 0)`; `SRem = a − q × b`
under `[wrap]`. When [#94](https://github.com/Hawkynt/Polypiler/issues/94) proves both operands non-negative the native operation is used
unchanged. Division by zero keeps its trap; `MinValue / −1` is handled first by `O0079.sdiv-min`.
`fdiv-ieee` — `FDiv`: if `b` is not zero, the native division; if `b` is zero: NaN when `a` is zero or NaN,
otherwise ∞ with sign `sign(a) xor sign(b)`, where `sign(b)` distinguishes `+0.0` from `−0.0` (taken from
the sign bit, not from a comparison). `FRem`: NaN when `b` is zero or `a` is infinite or either is NaN;
`a` when `b` is infinite and `a` finite; otherwise the native remainder with the dividend's sign.
`f32-round` — every `f32` value is represented by a binary64 that is exactly a binary32 value. Each of
`Add`, `Sub`, `Mul`, `FDiv`, `Sqrt` is computed in binary64 and then rounded to nearest-even binary32
(`ply_round_f32`, overflow to ±∞ included); because 53 ≥ 2 × 24 + 2 the double rounding is innocuous and
the result equals the correctly rounded binary32 operation. `Neg`, `Abs`, comparisons and `IntToFloat` of
integers below 2^24 need no rounding; other conversions into `f32` are rounded once. The rule refuses
`Fma.f32` and transcendental operations.
`f2i-trap` — NaN traps; the value traps iff `x ≥ MAX + 1` or `x ≤ MIN − 1` (when `MIN − 1` is not
representable in the source float format, `x < MIN` is the same set); otherwise truncate toward zero.
`MAX + 1` is a power of two and exact in every binary format. The trap label is the instruction's.
`bounds-assert` — `nativeBounds: none`: assert `i <u length` (the index reinterpreted unsigned, so a
negative index fails too); `upper-only`: the target already fails for `i ≥ length`, so only `i ≥s 0` is
asserted, and only when no range fact proves it. Bulk operations assert `start <=u length` and
`count <=u length − start` (no overflow in the test). The assertion is placed immediately before the
access with nothing in between, so the trap occurs where the checked access would have trapped; the
access is then `[unchecked]`.

## How it is proven

`exhaustive-8bit+smt-bitvector` for `sdiv-trunc` (5 s per obligation at 16/32/64 bit; a timeout leaves the helper form); `interpreter` cross-check against .NET for the three float rules; `structural` for `bounds-assert` (the assertion is the access's own trap condition).

## When it must not fire

- `−7 / 2`: flooring gives −4, the IR requires −3; `−7 rem 2` is −1, the floored modulo is 1.
- `7 / −2`: flooring gives −4 (remainder −1), truncation −3 (remainder 1). `6 / −3 = −2` needs no correction — the adjustment must test the remainder.
- `1.0 / −0.0` is −∞; choosing the sign from `b < 0` gives +∞ because `−0.0 < 0` is false.
- `0.1f + 0.2f` computed in binary64 without rounding is `0.30000000447…`; rounded to binary32 it is `0x3E99999A`, the binary32 sum. Rounding only at the end of an expression instead of after every operation changes results.
- `FloatToInt.i32[trap]` of `−2147483648.9` is −2147483648 (in range after truncation); testing `x < MIN` would trap.
- Perl `$a[5]` on a three-element array returns `undef` and an assignment extends the array; Python `a[-1]` returns the last element. Neither fails.

## Switching it off

`--disable-opt O0077` (or one rule: `--disable-opt O0077.f32-round`); `Kind = Required`: the rules run at every `--opt` level including `none`. With a rule disabled and no native form, the operation is PLY0530.

## Interactions with other passes

runs in phase `legalize` under the driver of [#259](https://github.com/Hawkynt/Polypiler/issues/259), after [#256](https://github.com/Hawkynt/Polypiler/issues/256) (`sdiv-min`) and before [#497](https://github.com/Hawkynt/Polypiler/issues/497); helper forms join the closure of [#260](https://github.com/Hawkynt/Polypiler/issues/260); [#502](https://github.com/Hawkynt/Polypiler/issues/502) decides beforehand which accesses still carry `[bounds]`.
