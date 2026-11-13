# O0110 — Recognize parity from xor folding, nibble lookup and toggling loops

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#573](https://github.com/Hawkynt/Polypiler/issues/573). Once implemented it is switched off with `--disable-opt O0110`.

## What it does

Parity bits protect serial frames, tapes and memory words, and LFSR feedback is the parity of a masked
state. It is written as a chain of xor-shifts ending in `& 1`, as a 16-bit magic constant indexed by the
folded nibble, or as a loop toggling a bit per set bit. x86 computes it in a flag, ARM in a population
count, a 6502 in a table. PRD §4.5, §19.2; Cipher's checksum family has a parity algorithm of its own.

## Example

### Before

```
%a1 = LShr.u32[strict] %x, 16
%a = Xor.u32 %x, %a1
%b1 = LShr.u32[strict] %a, 8
%b = Xor.u32 %a, %b1
%c1 = LShr.u32[strict] %b, 4
%c = Xor.u32 %b, %c1
%d1 = LShr.u32[strict] %c, 2
%d = Xor.u32 %c, %d1
%e1 = LShr.u32[strict] %d, 1
%e = Xor.u32 %d, %e1
%r = And.u32 %e, 1
```

### After

```
%p = PopCount.u32 %x
%r = And.u32 %p, 1
```

## When it applies

expression forms: the root is `And(…, 1)` over a pure DAG of at most
`4·log2 w + 4` instructions with one non-constant leaf `x`, built from `Xor`, `LShr`, `AShr`, `And`
and constants; the obligation `root(x) = And(PopCount(x), 1)` holds. Arithmetic shifts are accepted
exactly when the proof shows the sign fill never reaches bit 0. Loop forms: as in
[#278](https://github.com/Hawkynt/Polypiler/issues/278) — natural loop, one exit, pure body, no escape, logical shift in the shift form,
toggle value starting at a constant `p0` ∈ {0, 1} (the result is `Xor(p0, And(PopCount(x), 1))`).

## How it is proven

`exhaustive-8bit` for every form at width 8 plus `smt-bitvector` at 16, 32 and 64 bit
(loops unrolled `w` times). A failed, unknown or timed-out obligation (budget 5 s each) leaves the IR
unchanged and reports `not applied: proof timeout` or the failing precondition.

## When it must not fire

- Fold without the `>> 16` step on `u32`: `x = 0x00010000` gives 0; the parity is 1.
- Toggle loop in shift form with `AShr` on `i32`, `x = 0x80000000`: the loop never terminates.
- Nibble lookup with the constant `0x9669` (the complement table): it returns the *even* parity bit;
  `x = 1` gives 0.
- Final mask `& 3`: two bits survive; not a parity.

## Switching it off

`--disable-opt O0110`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` after [#278](https://github.com/Hawkynt/Polypiler/issues/278) (O0101) and the SWAR rule, so
every spelling of parity ends in the one form `And(PopCount(x), 1)`; enables flag-based parity
selection on x86 and table selection on 8-bit targets.
