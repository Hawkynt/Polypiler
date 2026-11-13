# O0062 — Specialize Python arbitrary-precision int to fixed-width integers or keep it exact

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#494](https://github.com/Hawkynt/Polypiler/issues/494). Once implemented it is switched off with `--disable-opt O0062`.

## What it does

A Python `int` never overflows. Algorithm code relies on that in two opposite ways: hashes emulate 32-bit
registers by masking (`(a + b) & 0xFFFFFFFF`), and number-theoretic code genuinely needs hundreds of bits.
The first kind must become native `u32` arithmetic; the second must stay exact or be refused — PRD §6.2:
"arbitrary precision is preserved or translation fails explicitly; no silent truncation occurs". Python's
floor division and modulo also differ from every C-family target for negative operands. PRD §4.6, §8 P3
("bigint-aware specialization").

## Example

### Before

```
// facts: %a, %b : Integer([0, 4294967295]) [Proven]
%s = Dyn.Add %a, %b                  // unbounded: up to 8589934590
%r = Dyn.BitAnd %s, 4294967295
```

### After

```
%r = Add.u32[wrap] %a, %b
```

## When it applies

(1) Operand facts from [#253](https://github.com/Hawkynt/Polypiler/issues/253) are `Integer` with ranges from [#94](https://github.com/Hawkynt/Polypiler/issues/94); `bool` is `Integer([0,
1])`. (2) Direct fixed-width form: the exact mathematical result range of the operation lies inside the
chosen type, so no wrap can occur. (3) Mask form: the masked expression consists only of `+`, `−`, `*`,
`<<` by a count proven in `[0, w − 1]`, `&`, `|`, `^` — operations whose low `w` bits depend only on the
low `w` bits of their operands — and every leaf is proven inside `[−2^(w−1), 2^w − 1]`. Negative
intermediates are fine: Python's `&` on a negative `int` uses infinite two's complement, which the wrapped
`u<w>` value equals. `//`, `%`, `>>` inside a masked expression block the mask form (they are not low-bit
closed). (4) `//`, `%`: floor semantics — quotient rounded toward −∞, remainder with the divisor's sign;
divisor proven non-zero or the `ZeroDivisionError` kept as a trap. (5) `<<` with a count that may reach the
width: the unbounded result is `x × 2^n`; a fixed-width `Shl` is used only under (2) or (3). (6) An `int`
that satisfies none of the above keeps the arbitrary-precision type; availability is read from the
capability graph of [#259](https://github.com/Hawkynt/Polypiler/issues/259).

## How it is proven

`range-facts`: exact-range containment, and low-bit closure of the listed operations (the same lemma as demanded-bits narrowing in [#161](https://github.com/Hawkynt/Polypiler/issues/161)); validated against `python`. No solver, no timeout path.

## When it must not fire

- `2**64` computed in `u64` is 0; Python gives 18446744073709551616.
- `-7 // 2` is −4 and `-7 % 2` is 1; truncating division gives −3 and −1. `7 % -2` is −1.
- `(a // b) & 0xFFFFFFFF` cannot be narrowed: the low 32 bits of a quotient depend on high bits of the dividend.
- `(1 << 40) & 0xFFFFFFFF` is 0; a 32-bit shift with a masked count gives `1 << 8 = 256`.
- `-1 >> 1` is −1 (arithmetic); `-1 & 0xFF` is 255.
- `math.factorial(25)` = 15511210043330985984000000 needs 84 bits.

## Switching it off

`--disable-opt O0062`; `Kind = Required`, so it runs at every level including `--opt none`. With it disabled every Python `int` stays arbitrary-precision; a target without big integers reports PLY0505.

## Interactions with other passes

runs in phase `specialize` after [#253](https://github.com/Hawkynt/Polypiler/issues/253); enables all integer passes on Python sources; what stays big is handled by [#263](https://github.com/Hawkynt/Polypiler/issues/263) or rejected.
