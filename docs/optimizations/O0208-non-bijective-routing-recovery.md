# O0208 — Recover routings that duplicate, invert or set bits as BitRoute, never as a permutation

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#548](https://github.com/Hawkynt/Polypiler/issues/548). Once implemented it is switched off with `--disable-opt O0208`.

## What it does

BitPerm's model is richer than a permutation: one source bit may feed several outputs, bits may be
inverted or forced. Code does the same — replicate a nibble, sign-spread a bit, set a flag while moving a
field. PRD §20.2 calls this `BitRouting`, "not generally invertible"; PRD §23.3 names the failure to test
for, "duplicate bits treated as bijection"; PRD §24 exit: "non-bijective cases don't invert". The rule
recovers such code as `BitRoute` and is where that prohibition is enforced and tested.

## Example

### Before

```
%a = And.u8 %x, 0x0F
%b = Shl.u8[strict] %a, 4
%c = Or.u8 %b, %a
%d = Xor.u8 %c, 0x01
%r = Or.u8 %d, 0x80
```

### After

```
%r = BitRoute.u8 %x, [~0,1,2,3,0,1,2,=1]
```

## When it applies

`BitProvenance.Analyze(%r)` returns a mapping with one input and no `Mixed`
bit; the mapping has `HasDuplication`, `HasInversion` or a `=1` entry; the slice has at least three
instructions. The replacement is always `BitRoute`; the rule asserts `BitPermutation.TryFrom` fails for
the mapping and reports the H1 reason of [#267](https://github.com/Hawkynt/Polypiler/issues/267).

## How it is proven

`exhaustive-<n>bit` (≤ 24 input bits) or `smt-bitvector` of the slice against the
registered decomposition; on a timeout the rewrite does not fire.

## When it must not fire

- Treating a duplication as invertible: after `[0,1,2,3,0,1,2,3]` the inputs `0xB4` and `0x04` both give `0x44` — no function restores the input.
- Dropping the inversion: `[~0,1,…]` on `x = 0x00` must give bit 0 set.
- Dropping the forced bit: `[…,=1]` on `x = 0x00` must give `0x81`.
- An output that mixes two inputs: `(x << 1) ^ x`.

## Switching it off

`--disable-opt O0208`; active at `--opt balanced`, `speed`, `size`, `memory`; off at `none`.

## Interactions with other passes

runs after [#549](https://github.com/Hawkynt/Polypiler/issues/549) (which takes the injective cases); feeds [#544](https://github.com/Hawkynt/Polypiler/issues/544) and [#530](https://github.com/Hawkynt/Polypiler/issues/530);
[#533](https://github.com/Hawkynt/Polypiler/issues/533) and the naming rules for permutations never match its result.
