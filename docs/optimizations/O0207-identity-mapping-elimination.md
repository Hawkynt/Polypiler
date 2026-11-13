# O0207 — Remove a bit mapping that is the identity, so that inverse pairs cancel

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#533](https://github.com/Hawkynt/Polypiler/issues/533). Once implemented it is switched off with `--disable-opt O0207`.

## What it does

PRD §22: "Given two consecutive inverse bit permutations have no externally visible boundary, when the
layout optimizer runs, then the pair is removed and all source-level observable effects are preserved."
PRD §20.4 states the law, `P⁻¹(P(x)) = x`, for bijections only. Once [#544](https://github.com/Hawkynt/Polypiler/issues/544) has merged the pair,
what is left is a `BitPermute` whose mapping is the identity; this rule removes it — and is the place
where it is checked that nothing but a true identity disappears.

## Example

### Before

```
%a = BitPermute.u8 %x, [5,6,7,0,1,2,3,4]
%b = BitPermute.u8 %a, [3,4,5,6,7,0,1,2]
%y = Xor.u8 %b, %k
```
(after O0206: `%b = BitPermute.u8 %x, [0,1,2,3,4,5,6,7]`)

### After

```
%y = Xor.u8 %x, %k
```

## When it applies

the mapping `Describe`s as `Identity` — square, every entry `i` a plain source
`i`; operand and result types have the same width (a signed/unsigned reinterpretation is kept as a
conversion). No don't-care, constant or inverted entry.

## How it is proven

exact list comparison, plus `exhaustive-8bit` of `BitPermute[identity] = x` at 8 bits and
`smt-bitvector` at the instance width; a timeout leaves the operation in place (its decomposition is `x`
anyway).

## When it must not fire

- A duplicating routing followed by a mask: `%a = BitRoute.u8 %x, [0,1,2,3,0,1,2,3]; %r = And.u8 %a, 0x0F` — for `x = 0xB4` the result is `0x04`, not `0xB4`.
- Identity except one constant: `BitRoute.u8 %x, [0,1,2,3,4,5,6,=0]` — `x = 0x80` gives `0x00`.
- A rotation by the full order minus one: `RotateLeft 3` then `RotateRight 4` at 8 bits leaves `RotateRight 1`.
- Inverse steps on different values: `%a = ReverseBits %x; %r = ReverseBits %y`.

## Switching it off

`--disable-opt O0207`; active at `--opt balanced`, `speed`, `size`, `memory`; off at `none`.

## Interactions with other passes

runs after [#544](https://github.com/Hawkynt/Polypiler/issues/544); leaves dead operations to dead-code elimination; enables further
composition across the removed operation.
