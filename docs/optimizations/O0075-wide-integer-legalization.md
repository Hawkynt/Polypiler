# O0075 — Legalize integers wider than the target's native width into limbs

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#263](https://github.com/Hawkynt/Polypiler/issues/263). Once implemented it is switched off with `--disable-opt O0075`.

## What it does

JavaScript's bitwise integers are 32 bits wide, 16-bit DOS code has 16-bit registers, a 6502 has eight, and
even 64-bit targets meet 128-bit products and `UInt4096`. PRD §21.3 models this as `Integer.Wide(bits)`;
§8 P2 needs it at once ("PB integer precision", Cipher's 64-bit hashes translated to JavaScript and back).
A wide operation must be decomposed into limbs with explicit carries — and only integers get carries: PRD
§4.7 insists that `UInt4096`, `BitVector4096` and `Vector4096<byte>` "cannot share arithmetic semantics by
fiat".

## Example

### Before

```
%s = Add.u64[wrap] %a, %b
```

### After

```
// Integer.Wide(32): %a = (%a.lo, %a.hi), %b = (%b.lo, %b.hi)
%s.lo = Add.u32[wrap] %a.lo, %b.lo
%c    = ICmp.ult.u32 %s.lo, %a.lo                 // carry out of the low limb
%t    = Add.u32[wrap] %a.hi, %b.hi
%s.hi = Add.u32[wrap] %t, (ZExt.u32 %c)
```

## When it applies

With limb width `n` = the largest `Integer.Wide` the profile offers and value width `w`: limbs are unsigned
`u<n>`; the top limb carries the sign for signed types. `Add`: carry of limb `i` is `(s_i < a_i) or (carry_in
and s_i == a_i)`, computed with **unsigned** comparisons. `Sub`: borrow `(a_i < b_i) or (borrow_in and a_i
== b_i)`. Comparisons: most significant limb first — signed comparison on the top limb for signed types,
unsigned on all others. Shifts by a constant `k`: whole-limb moves by `k / n` plus a bit shift by `k mod n`
combining neighbours; a limb-level shift is never emitted with an amount equal to `n` (the `k mod n = 0`
case is a pure move). Variable shifts select by `k / n` and handle `k mod n = 0` separately; the amount
contract of the wide shift (`[mask]` or `[strict]`) is applied to the wide width, not the limb width.
`Mul[wrap]`: schoolbook over limbs keeping only the low `w` bits; each limb product needs `2n` bits —
taken from a widening multiply if the profile has `Native(MulWide.u<n>)`, otherwise from half-limb
(`n/2`-bit) partial products. `[checked]`: the trap condition is the wide operation's — carry out of the
top limb for unsigned add, sign-overflow rule on the top limb for signed. Only types whose family is
*integer* are legalized here; `BitVector` and `Vector` types of equal width are refused.

## How it is proven

`exhaustive-16bit+smt-bitvector`: every operation's limb sequence is exhaustive for 16-from-8 and proven by [#71](https://github.com/Hawkynt/Polypiler/issues/71) for 32-from-16 and 64-from-32 (5 s per obligation; `Mul` and variable shifts are proven per limb count 2; on timeout the operation falls back to the helper alternative, `not applied: proof timeout`).

## When it must not fire

- Carry by signed comparison: `0x00000000_FFFFFFFF + 1` — the low limb wraps to 0 and `0 < 0xFFFFFFFF` must be evaluated unsigned; signed it is false (−1) and the carry is lost.
- Carry-in ignored when `s_i == a_i`: three-limb `0x0000_FFFF_FFFF + 0x0000_0000_0001` needs the carry to ripple through the middle limb.
- Limb shift by `n`: `x << 32` implemented as `hi = lo << 32` on a target whose shifts mask the amount gives `hi = lo << 0`.
- Low × low overflow: `0xFFFFFFFF × 0xFFFFFFFF` needs 64 bits (`0xFFFFFFFE_00000001`); a 32-bit multiply keeps only `0x00000001`.
- Signed comparison of `−1` (`0xFFFFFFFF_FFFFFFFF`) and `0`: comparing the top limb unsigned says −1 is larger.
- `Vector<64 x u8>` addition given carries between bytes because it has the same width as a `u512`.

## Switching it off

`--disable-opt O0075`; `Kind = Required`: the rule runs at every `--opt` level including `none`. With it disabled the legalizer has no alternative for these operations on targets that need it and reports PLY0530.

## Interactions with other passes

runs in phase `legalize` under the driver of [#259](https://github.com/Hawkynt/Polypiler/issues/259) after [#161](https://github.com/Hawkynt/Polypiler/issues/161) and [#258](https://github.com/Hawkynt/Polypiler/issues/258); division helpers join the closure of [#260](https://github.com/Hawkynt/Polypiler/issues/260); afterwards [#154](https://github.com/Hawkynt/Polypiler/issues/154), [#158](https://github.com/Hawkynt/Polypiler/issues/158) and [#156](https://github.com/Hawkynt/Polypiler/issues/156) remove dead limbs.
