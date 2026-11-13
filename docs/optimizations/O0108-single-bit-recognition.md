# O0108 — Recognize single-bit test, set, clear and flip with a variable index

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#558](https://github.com/Hawkynt/Polypiler/issues/558). Once implemented it is switched off with `--disable-opt O0108`.

## What it does

Bit sets, Bloom filters, flag words and bit streams address one bit by a computed index: `(x >> n) & 1`,
`x | (1 << n)`, `x & ~(1 << n)`, `x ^ (1 << n)`. x86 has `BT`/`BTS`/`BTR`/`BTC`, PowerBASIC has `BIT`,
`BIT SET` and `BIT RESET`, and a 6502 backend wants a mask table rather than a variable shift. The
classic defect in this idiom — a 32-bit `1` shifted and then widened — is exactly what a naive matcher
would paper over. PRD §4.5, §19.2.

## Example

### Before

```
%m = Shl.u32[mask] 1, %n
%r = Or.u32 %x, %m
```

### After

```
%r = BitSet.u32[mask] %x, %n
```

## When it applies

the shifted constant is exactly 1 and has the same type `u<w>`/`i<w>` as
`x` — no conversion lies between the shift and the combining operation; the shift's contract is
`[mask]`, or `[strict]` with the range fact `0 ≤ n ≤ w-1`, and the intrinsic takes the same contract;
for the test form through `LShr`, the shift is logical *or* arithmetic (bit 0 of the result is bit `n`
of `x` either way for `n ≤ w-1`), and the `And` constant is exactly 1. The shifted mask may have other
users; only the root is rewritten.

## How it is proven

`exhaustive-8bit` (all 256 values × all 8 indices × four operations × both test
spellings) plus `smt-bitvector` at 16, 32 and 64 bit. A failed, unknown or timed-out obligation
(budget 5 s each) leaves the IR unchanged and reports `not applied: proof timeout` or the failing
precondition.

## When it must not fire

- Narrow mask widened: `%m32 = Shl.i32[mask] 1, %n; %m = SExt.u64 %m32; Or.u64 %x, %m` with `x = 0`,
  `n = 31` gives `0xFFFFFFFF80000000`; `BitSet.u64 0, 31` gives `0x0000000080000000`. With `n = 40`
  the 32-bit shift masks the amount to 8 and sets bit 8, not bit 40.
- Shifted constant 2: `Or(x, Shl(2, n))` sets bit `n + 1` and nothing at `n = w-1`.
- `And(LShr(x, n), 3)`: a two-bit field, not a bit test.
- `[strict]` shift without a range fact on `n`: the original is a verifier error or a trap for `n ≥
  w`; the intrinsic must not hide it.

## Switching it off

`--disable-opt O0108`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` after range propagation; enables bit-test instruction
selection and PowerBASIC `BIT` statements.
