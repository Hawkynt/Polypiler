# O0142 — Recognize byte-wise XOR loops over two buffers

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#271](https://github.com/Hawkynt/Polypiler/issues/271). Once implemented it is switched off with `--disable-opt O0142`.

## What it does

XOR-ing a keystream into a buffer is the last step of every stream cipher and block mode: `for (i = 0; i
< n; i++) dst[i] = a[i] ^ b[i]`. Cipher has four spellings of it (`XorArrays`, `FastXorArrays`,
`FastXorInPlace`, `FastXorWords32`). One byte per iteration wastes a 64-bit or SIMD target by a factor
of 8 to 64, and the loop's meaning depends on how the buffers overlap. PRD §4.5, §8 P2 ("byte XOR").

## Example

### Before

```
loop:
  %i = Phi.u32 [0, entry], [%i2, body]
  %c = ICmp.ult.u32 %i, %n
  CondBr %c, body, exit
body:
  %pa = ElementAddr %a, %i
  %pb = ElementAddr %b, %i
  %pd = ElementAddr %dst, %i
  %x = Load.u8 %pa
  %y = Load.u8 %pb
  %z = Xor.u8 %x, %y
  Store.u8 %pd, %z
  %i2 = Add.u32 %i, 1
  Br loop
```

### After

```
Memory.Xor.u8 %dst, %a, %b, %n
```

## When it applies

(1) a natural loop with one exit, induction variable from 0 with step 1, a
loop-invariant bound `n` evaluated once (a length re-read each iteration must be proven unchanged by
the loop), and no other instruction with effects in the body; (2) the three addresses are `base + i`
with loop-invariant bases; (3) overlap: `dst` is disjoint from `a` and from `b` over `[0, n)`, or
`dst` is exactly `a` (or exactly `b`) with the same index — any other overlap changes the meaning; (4)
every access of the loop is proven in bounds for the whole range `[0, n)` before the loop starts
(range facts relating `n` to the span lengths), or the accesses carry no bounds trap in their source
profile; otherwise the original performs the first `k` iterations and then traps with a partially
written buffer, which the single operation cannot reproduce; (5) all three spans are ordinary,
non-volatile memory; (6) the induction variable does not wrap before reaching `n`; (7) no value
defined in the loop is used after it.

## How it is proven

`loop-inductive`: the invariant "after `i` iterations `dst[0..i)` holds the XOR of
the original `a[0..i)` and `b[0..i)`, and all other memory is unchanged" is discharged by SMT for one
symbolic iteration, with the alias, bounds and effect preconditions as fact obligations; plus bounded
`exhaustive` interpretation for `n` ∈ 0..3 over all 8-bit element values. A failed, unknown or
timed-out obligation (budget 5 s each) leaves the IR unchanged and reports `not applied: proof
timeout` or the failing precondition.

## When it must not fire

- Shifted overlap: `dst = a + 1`, `a = 01 02 03 04 05`, `b` all zero, `n = 4`: the loop copies byte 0
  forward and leaves `01 01 01 01 01`; an element-wise XOR of the original contents gives `01 01 02 03
  04`.
- Trap after partial work: `n = 8` over 4-byte spans with trapping bounds writes four bytes and
  raises.
- A bound that changes: `i < dst.length` where the body may grow or shrink `dst`.
- Step 2 or a start index other than 0 in one of the three addresses (`b[i + 1]`): a different
  operation.

## Switching it off

`--disable-opt O0142`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` after loop canonicalization, before [#281](https://github.com/Hawkynt/Polypiler/issues/281)
(O0150) by rule order, so byte XOR loops become the memory operation and everything else becomes
`Map`; enables word-wide and SIMD lowering.
