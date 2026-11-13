# O0406 — Search for an element several positions at a time with the zero-lane word test

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0406`.

## What it does

`Memory.Find` ([#189](https://github.com/Hawkynt/Polypiler/issues/189)) decomposes into a loop that compares one element per step. On a target
without a SIMD unit and without a tuned library search — an 8086, a microcontroller, WebAssembly without
SIMD, PowerBASIC source — that loop is what runs. The classic word trick examines two, four or eight bytes
per step with five integer operations: XOR with the sought byte broadcast to every lane, then the
zero-lane test, then a bit scan for the position. The trick has well-known ways to be subtly wrong: the
flags above the first match are unreliable, and a word load reads bytes the element loop would never have
touched. PRD §4.8 (SWAR), §8 P3.

## Example

### Before

```
%r = Memory.Find.u8[read=full] %src, %b, %n         // first i < n with src[i] = b, else n
```

### After

```
  %pat = Mul.u32[wrap] (ZExt.u32 %b), 0x01010101    // the value in every lane
  %nw  = And.uptr %n, (Not.uptr 3)                  // elements covered by whole words
head:
  %i   = Phi.uptr [entry: 0], [next: %i4]
  %c   = ICmp.ult.uptr %i, %nw
  Br %c, word, tail
word:
  %w   = LoadLE.u32[inbounds #1] %src, %i           // lowest address = least significant lane, on every target
  %x   = Xor.u32 %w, %pat
  %d   = Sub.u32[wrap] %x, 0x01010101
  %t   = And.u32 (And.u32 %d, (Not.u32 %x)), 0x80808080
  %hit = ICmp.ne.u32 %t, 0
  Br %hit, found, next
next:
  %i4  = Add.uptr %i, 4
  Br head
found:
  %tz  = TrailingZeroCount.u32 %t
  %k   = LShr.u32[strict] %tz, 3
  %r   = Add.uptr %i, (ZExt.uptr %k)                // joins the result phi
tail:                                               // element loop over [%nw, %n); yields %n when nothing matches
```

## When it applies

(1) The element type is `u8` or `u16` and `W` is 16, 32 or 64 with at least two lanes, `W` ≤ `swar.registerBits`
of the cost profile. (2) Read extent: the operation is `[read=full]`, or it is `[read=prefix]` and a
bounds proof shows all `n` elements readable ([#94](https://github.com/Hawkynt/Polypiler/issues/94), proof table of [#121](https://github.com/Hawkynt/Polypiler/issues/121)). Under an
unproven `prefix` contract the source reads nothing beyond the first match and the rule does not fire.
(3) Word loads cover only elements below `n`; the remainder is searched element by element. (4) The word
is loaded with `LoadLE` ([#113](https://github.com/Hawkynt/Polypiler/issues/113)) regardless of the target's byte order, so lane `j` is element
`i + j`. (5) `t` is non-zero iff some lane of `x` is zero, and its **lowest** set bit is the top bit of the
first such lane; higher set bits may be false and are never used. The position is therefore
`TrailingZeroCount(t)` ([#95](https://github.com/Hawkynt/Polypiler/issues/95)) divided by the lane width — not a leading count, not a
population count. (6) The region is not volatile (`Memory.Find` already rejects that with PLY0258).
(7) [#155](https://github.com/Hawkynt/Polypiler/issues/155) prices the `swar` form below the element loop and below any native search the profile
lists for `Memory.Find`.

## How it is proven

`exhaustive-16bit+smt-bitvector+structural`: the word test and the position formula are exhaustive for two 8-bit lanes and proven by [#71](https://github.com/Hawkynt/Polypiler/issues/71) for the other shapes with a 5 s budget each; a timeout removes that shape from the rule table (`not applied: proof timeout`). That the loop visits words in ascending order and stops at the first hit is structural.

## When it must not fire

- A big-endian word load with a leading-zero count: searching 0x20 in the bytes `21 20` gives `x = 0x0100`, `t = 0x8080` — the upper flag is a borrow artefact — and position 0; the first match is at 1.
- Counting the flags to count matches: the bytes `20 21` contain one 0x20 but yield `t = 0x8080`, two flags.
- The shorter test `(x − L) & H` without `& ~x`: the byte 0xA1 searched for 0x20 gives lane `x = 0x81`, `0x81 − 1 = 0x80`, a match that does not exist.
- `Memory.Find[read=prefix] %p, 0, 65535` on a 10-byte buffer whose terminator is at index 9, the last byte before an unmapped page: the element loop stops at 9; a word load at 8 reads into the next page.
- `n = 5`, `W = 32`: a second word load at index 4 reads elements 5 … 7, which are outside the range.
- Using the target's native byte order on a big-endian CPU without the swap: lane 0 is then the last element of the word and the first-match rule is reversed.

## Switching it off

`--disable-opt O0406`; active at `--opt balanced` and `speed`, off at `--opt none`, `size` and `memory` (the word loop is larger than the element loop).

## Targets

targets without a tuned search primitive: assembly and machine-code backends for CPUs without SIMD (8086 with 16-bit words, 68000, ARM without NEON), WebAssembly without the SIMD proposal, PowerBASIC and other BASIC source targets. Where the target has one — C `memchr`, .NET `Span.IndexOf`, Rust `memchr`, JavaScript `TypedArray.indexOf`, Java intrinsics — the profile lists it as native for `Memory.Find`, the cost model picks it, and the generated source stays one readable call. On 8-bit CPUs `swar.registerBits` is 8 and the rule never fires.

## Interactions with other passes

runs in phase `legalize`, like [#511](https://github.com/Hawkynt/Polypiler/issues/511), when the legalizer needs a decomposition of `Memory.Find`; afterwards [#154](https://github.com/Hawkynt/Polypiler/issues/154) folds the broadcast of a constant value.
