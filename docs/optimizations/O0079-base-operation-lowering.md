# O0079 — Lower base operations a target lacks through one shared rule set

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#256](https://github.com/Hawkynt/Polypiler/issues/256). Once implemented it is switched off with `--disable-opt O0079`.

## What it does

Some operations every IR program uses are missing from surprisingly many targets: PowerBASIC and classic
BASICs have no arithmetic right shift, C has no overflow-checked arithmetic and leaves `INT_MIN / -1`
undefined, JavaScript's `*` is a 53-bit float multiply, PHP and Perl promote an overflowing integer product
to a float, and a saturating float-to-integer conversion exists natively almost nowhere. Each backend was
about to invent its own lowering for these — nine slightly different, separately unproven versions. This
issue holds the rules once, proves them once, and lets a backend contribute only what is target-specific:
the statement that it has the operation natively, or the spelling of a helper. PRD §17.1 (`MapNative`,
`GenerateHelper`, `Emulate`), §4.9, §21.3.

## Example

### Before

```
%r = AShr.i32[strict] %x, %n                    // target has LShr and Shl only
%s = Add.i32[checked] %a, %b                    // target has wrapping Add only
```

### After

```
// O0079.ashr, inline form (n proven in [1, 31]; n = 0 selects %x unchanged)
%l = LShr.u32[strict] %x, %n
%g = LShr.u32[strict] %x, 31                    // 1 when %x is negative
%f = Neg.u32[wrap] %g                           // all ones when negative
%k = Sub.u32[wrap] 32, %n
%h = Shl.u32[strict] %f, %k                     // the sign bits to fill in
%r = Or.u32 %l, %h
// O0079.checked-add, inline form
%s = Add.i32[wrap] %a, %b
%p = Xor.i32 %a, %s
%q = Xor.i32 %b, %s
%t = And.i32 %p, %q
%o = ICmp.slt.i32 %t, 0
Br %o, overflow, cont                           // overflow: Raise Overflow
```

## When it applies

A rule applies only when the profile does not list the operation as native (`Native(<Op.type>[contract])`)
and its own requirements are native or legalizable. Per rule, with width `w`:
`ashr` — `n = 0` returns `x`; for `1 ≤ n ≤ w − 1`: `(x >>> n) | ((0 − (x >>> (w − 1))) << (w − n))`; a
variable `n` is tested for 0 first so no shift by `w` is ever emitted; the shift-amount contract of the
original (`[mask]` or `[strict]`) is applied to `n` before the formula.
`checked-add` — unsigned: overflow iff `s <u a`; signed: iff `((a ^ s) & (b ^ s)) <s 0`.
`checked-sub` — unsigned: iff `a <u b`; signed: iff `((a ^ b) & (a ^ d)) <s 0` with `d = a − b`.
`checked-mul` — with a double-width type or `Native(MulWide)`: compute the full product and test that it
fits; otherwise unsigned: `b ≠ 0 and a >u max / b` tested **before** multiplying; signed: the four-quadrant
bound tests of §2-13, with `MinValue × −1` flagged explicitly. The test never divides by zero.
`mul-wrap` — operands split into halves of `w/2` bits: `lo(a)·lo(b) + ((hi(a)·lo(b) + lo(a)·hi(b)) << w/2)`
reduced modulo 2^w after every step, each partial product small enough for the target's exact integer
range (the profile key `exactIntegerBits`, 53 for IEEE-double numbers); 64-bit operands on targets without
64-bit integers go to [#263](https://github.com/Hawkynt/Polypiler/issues/263) instead.
`sdiv-min` — applies when the profile says `divMinByMinusOne: traps | undefined`: `SDiv[wrap]` becomes `b
= −1 ? Neg[wrap](a) : a / b`, `SRem` becomes `b = −1 ? 0 : a % b`; division by zero is not this rule's
concern and keeps its own trap.
`f2i-sat` — in this order: NaN test (`x ≠ x`) → 0 or the trap; `x ≤ MIN` → `MIN`; `x ≥ MAX + 1` → `MAX`;
otherwise truncate toward zero. `MIN` and `MAX + 1` are powers of two and exactly representable in every
binary float format, so the comparisons are exact; the truncation is performed only on in-range values.
In every rule the raise or the result appears exactly where the original instruction was; no effect is
moved across it.

## How it is proven

`exhaustive-8bit+smt-bitvector` per rule and signedness (5 s per obligation at 16/32/64 bit; a timeout removes the inline form at that width and leaves the helper form, whose body is the same identity proven at 8 bit and tested at the wider widths against `BigInteger`); `f2i-sat`: `interpreter` cross-check against .NET over the boundary set and random values.

## When it must not fire

- `ashr` as a plain logical shift: `−8 >> 1` must be −4 (`0xFFFFFFFC`), the logical shift gives `0x7FFFFFFC`.
- `ashr` by 0 through the formula: `(0 − sign) << 32` is a shift by the width — undefined or masked to a shift by 0 on most targets, which would OR all sign bits into the result.
- Checked signed add tested as `s < a`: `(−1) + 1 = 0` and `0 < −1` is false — correct by luck; `1 + (−1) = 0`, `0 < 1` reports an overflow that did not happen.
- Checked multiply tested after the fact with `p / a ≠ b` in a language where the multiplication already overflowed into a float or into undefined behaviour.
- 32-bit multiply on IEEE-double numbers: `0xFFFFFFFF × 0xFFFFFFFF` rounds; the low 32 bits must be 1.
- `−2147483648 / −1` with the native divide: a hardware trap on x86, undefined in C; under `[wrap]` the result is −2147483648 and the remainder 0.
- Saturating conversion by truncating first: `(int)3e10` is undefined in C and `−2147483648` on x86; the saturated result is 2147483647. NaN must give 0, not `MIN`.

## Switching it off

`--disable-opt O0079`; `Kind = Required`: the rule runs at every `--opt` level including `none`. With it disabled the legalizer has no alternative for these operations on targets that need it and reports PLY0530. Each rule can also be disabled alone by its sub-ID (`--disable-opt O0079.ashr`).

## Interactions with other passes

runs in phase `legalize` under the driver of [#259](https://github.com/Hawkynt/Polypiler/issues/259), before [#497](https://github.com/Hawkynt/Polypiler/issues/497) (which lowers the raises) and after [#263](https://github.com/Hawkynt/Polypiler/issues/263) has reduced widths; helper forms join the closure of [#260](https://github.com/Hawkynt/Polypiler/issues/260); afterwards [#154](https://github.com/Hawkynt/Polypiler/issues/154) and [#158](https://github.com/Hawkynt/Polypiler/issues/158) simplify constant amounts and repeated sign computations.
