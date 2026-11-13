# O0060 — Specialize JavaScript Number arithmetic to typed integer and float operations

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#255](https://github.com/Hawkynt/Polypiler/issues/255). Once implemented it is switched off with `--disable-opt O0060`.

## What it does

JavaScript has one numeric type, the IEEE double, and a set of operators that secretly convert it: every
bitwise operator applies `ToInt32`, `>>>` applies `ToUint32`, shift counts are taken modulo 32. Cipher's
whole code base is written in that idiom — `(a + b) | 0`, `x >>> 0`, `Math.imul`. Translating it to C# or
Rust means recovering the 32-bit integer operations the author meant, without ever being wrong about the
cases where a double behaves differently. PRD §4.6 (JS: ToInt32/ToUint32, NaN, `Number` vs BigInt), §6.2
first scenario, §8 P2 ("static JS numeric coercion").

## Example

### Before

```
// facts from DYN-INFER: %a, %b : Integer([-2147483648, 2147483647]) [Proven]
%s = Dyn.Add %a, %b            // double addition
%r = Dyn.BitOr %s, 0           // ToInt32(%s) | 0
```

### After

```
%r = Add.i32[wrap] %a, %b
```

## When it applies

Every rule requires `Proven` facts from [#253](https://github.com/Hawkynt/Polypiler/issues/253) for its operands (or `Contextual`/`Guarded` facts
whose conditions are established — the result then inherits that state). Exactness argument per rule:
integers of magnitude below 2^53 are exact in a double, so for 32-bit `a`, `b` the double `a ± b` is the
exact integer and `ToInt32` of it is the wrapped 32-bit result. For multiplication the exact product can
need 64 bits; the double rounds it, so the rule needs `|a × b| < 2^53` from [#94](https://github.com/Hawkynt/Polypiler/issues/94). Shift counts are
masked with 31 by the language, hence the `[mask]` contract; `>>>` produces a value in `[0, 2^32 − 1]`,
typed `u32`. A fact that admits NaN, ±∞, a fraction or `−0` where it is observable (`1 / x`,
`Object.is`) keeps the operand in `f64`; `ToInt32` of such a value is emitted as the explicit conversion
(truncate toward zero, then wrap modulo 2^32; NaN and ±∞ → 0) from [#116](https://github.com/Hawkynt/Polypiler/issues/116). Division needs `b ≠ 0`
proven; `−2147483648 / −1 | 0` is −2147483648, which `SDiv.i32[wrap]` reproduces. `%` takes the sign of the
dividend, as `SRem` does; a zero result of a negative dividend is `−0` in JavaScript, so `SRem` is used
only when the result's consumer is an integer context.

## How it is proven

`range-facts`: each rule follows from the ECMA-262 definitions and the exactness of integers below 2^53 in binary64; the rules are validated against `node` (measurement below). No solver, no timeout path.

## When it must not fire

- `(0xFFFFFFFF * 0xFFFFFFFF) | 0` is 0 in JavaScript (the product 2^64 − 2^33 + 1 rounds to 2^64 − 2^33); a 32-bit wrapping multiply gives 1. `Math.imul` gives 1.
- `2147483647 + 1` without `| 0` is 2147483648, not −2147483648.
- `1 << 32` is 1, not 0: the count is taken modulo 32.
- `−1 >>> 0` is 4294967295; typed as `i32` it would be −1.
- `(5 / 0) | 0` is 0 and `(0 / 0) | 0` is 0; an integer division would trap.
- `NaN | 0` is 0, `3.7 | 0` is 3, `−3.7 | 0` is −3 (truncation, not floor).
- `1n + 1` throws `TypeError`; treating both as integers computes 2.

## Switching it off

`--disable-opt O0060`; `Kind = Required`, so it runs at every level including `--opt none`. With it disabled, JavaScript numeric operations stay dynamic and a typed target reports PLY0500.

## Interactions with other passes

runs in phase `specialize` right after [#253](https://github.com/Hawkynt/Polypiler/issues/253); enables every integer pass and recognition rule on JavaScript sources; `Guarded` inputs are completed by [#778](https://github.com/Hawkynt/Polypiler/issues/778).
