# O0200 — Recover a bit mapping from masked, shifted and rotated copies of one value

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#549](https://github.com/Hawkynt/Polypiler/issues/549). Once implemented it is switched off with `--disable-opt O0200`.

## What it does

The commonest way to move bits is `((x & m1) << a) | ((x & m2) >> b) | …` — BitPerm emits exactly this
("Composite" solutions), and so does every programmer. PRD §24 Q1 requires reverse-recognizing "a real
BitPerm shift/mask mapping"; PRD §22: "every output bit is traced to exactly one input bit … mapping
execution matches the original for all 8-bit inputs". Until such an expression is one `BitPermute`, it
cannot be composed, inverted or re-synthesized for another target.

## Example

### Before

```
%a = And.u8 %x, 0x0F
%b = Shl.u8[strict] %a, 4
%c = And.u8 %x, 0xF0
%d = LShr.u8[strict] %c, 4
%r = Or.u8 %b, %d
```

### After

```
%r = BitPermute.u8 %x, [4,5,6,7,0,1,2,3]
```

## When it applies

`BitProvenance.Analyze(%r)` returns a mapping with one input of the root's
width; the slice consists of the root combiner and of `And`, `Or`, `Xor`, `Add`, `Shl`, `LShr`,
`RotateLeft`, `RotateRight` with constant operands, at least three instructions in all; the mapping is
injective, has no inverted entry and no `=1`. Bijective → `BitPermute`; otherwise `BitRoute` with `=0`
entries. An `Add` is accepted only where provenance treated it as `Or` on every bit.

## How it is proven

`exhaustive-<n>bit` for inputs up to 24 bits, else `smt-bitvector`, comparing the slice
with the registered decomposition of the replacement; on a timeout (5 s) the rewrite does not fire.

## When it must not fire

- Two sources on one bit: BitPerm's README example `((x & 0x0000FF00) << 8) | rol32(x & 0x000000FF, 16)` —
  for `x = 0x12345678` bits 16–23 hold `0x56 | 0x78 = 0x7E`, not a moved byte.
- Overlapping `Add`: `((x & 0x0F) << 4) + (x & 0xF0)` with `x = 0xFF` gives `0xE0` (a carry), an `Or` would give `0xF0`.
- `Xor` of overlapping terms: `(x << 1) ^ x` — a Boolean transform.
- Two inputs: `(x << 4) | (y >> 4)`.

## Switching it off

`--disable-opt O0200`; active at `--opt balanced`, `speed`, `size`, `memory`; off at `none`.

## Interactions with other passes

runs after constant folding and [#279](https://github.com/Hawkynt/Polypiler/issues/279) in the `recognize` phase; enables the naming
rules, [#544](https://github.com/Hawkynt/Polypiler/issues/544), [#533](https://github.com/Hawkynt/Polypiler/issues/533) and [#530](https://github.com/Hawkynt/Polypiler/issues/530).
