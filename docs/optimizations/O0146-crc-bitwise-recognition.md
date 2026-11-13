# O0146 — Recognize bitwise CRC loops as a parameterized CRC update

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#563](https://github.com/Hawkynt/Polypiler/issues/563). Once implemented it is switched off with `--disable-opt O0146`.

## What it does

The reference form of a CRC shifts the register eight times per byte and XORs in the polynomial when a
bit falls out; Cipher's `crc.js` and CompressionWorkbench's table generator both contain it. It is the
slowest way to compute a CRC and the easiest to recognize — and the place where recognition goes wrong
by assumption: a loop that "looks like CRC-32" may use another polynomial, the other bit order, seven
iterations, or add the byte instead of XOR-ing it. PRD §4.5 ("CRC folding"), §19.2 ("CRC (with
parameters)"); the design requirement is that parameters are recovered, never defaulted.

## Example

### Before

```
outer:
  %i = Phi.u32 [0, entry], [%i2, next]
  %c = Phi.u32 [%c0, entry], [%c8, next]
  %more = ICmp.ult.u32 %i, %n
  CondBr %more, byte, exit
byte:
  %p = ElementAddr %data, %i
  %b = Load.u8 %p
  %bz = ZExt.u32 %b
  %x = Xor.u32 %c, %bz
  Br bit
bit:
  %k = Phi.u32 [0, byte], [%k2, bit]
  %r = Phi.u32 [%x, byte], [%r2, bit]
  %low = And.u32 %r, 1
  %odd = ICmp.ne.u32 %low, 0
  %sh = LShr.u32[strict] %r, 1
  %xp = Xor.u32 %sh, 0xEDB88320
  %r2 = Select.u32 %odd, %xp, %sh
  %k2 = Add.u32 %k, 1
  %again = ICmp.ult.u32 %k2, 8
  CondBr %again, bit, next
next:
  %c8 = Phi.u32 [%r2, bit]
  %i2 = Add.u32 %i, 1
  Br outer
exit:
  Ret %c
```

### After

```
%c = Crc.Update[w=32, poly=0x04C11DB7, reflected=true] %c0, %data, %n
Ret %c
```

## When it applies

(1) the bit step is exactly the reflected or the normal
shift-and-conditional-XOR with a constant `P` and tests the bit that the shift discards; (2) the step
is applied exactly 8 times per byte; (3) the byte enters by `Xor` into the low byte (reflected) or the
top byte (normal) of the register before the 8 steps; (4) the register is confined to `w` bits — by
its type, or by masks after each left shift; (5) the per-byte function is proven equal to the
catalogue definition with the recovered parameters; (6) the outer loop is a counted unit-step loop
over a byte span whose only loop-carried state is the register, with no store or call, and every byte
is proven readable or the loads carry no trap; the span is ordinary memory; (7) the polynomial
reported is the normal-form polynomial (the bit reversal of `P` for reflected loops), without the
implicit top bit.

## How it is proven

`smt-bitvector` for the per-byte step against the reference bit-serial definition at
the matched width (one symbolic register value and one symbolic byte; the 8 steps unrolled),
`exhaustive-8bit` for `w = 8` (all 65 536 register/byte pairs), and `loop-inductive` for the outer
loop (the register after `i` bytes equals the update of the first `i` bytes). A failed, unknown or
timed-out obligation (budget 5 s each) leaves the IR unchanged and reports `not applied: proof
timeout` or the failing precondition.

## When it must not fire

- Seven iterations: with `P = 0xEDB88320`, initial value and final XOR `0xFFFFFFFF`, the message `a`
  gives `0xD16F7C87`; CRC-32 gives `0xE8B7BE43`.
- A famous polynomial with another initial value is not the famous CRC: the update is recognized, but
  the report must not say CRC-32 unless initial value and final XOR match too.
- Byte added instead of XOR-ed (`c + byte`): carries propagate into the register; not linear over
  GF(2).
- Testing a bit other than the one shifted out (`c & 2` with `c >> 1`): a different linear map.
- Reflected shift with a polynomial written in normal form: still *a* CRC, of a different polynomial —
  the recovered parameter must be the bit reversal of the constant actually used, not the constant as
  written.

## Switching it off

`--disable-opt O0146`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` after loop canonicalization and if-conversion; before
[#282](https://github.com/Hawkynt/Polypiler/issues/282) (O0152) by rule order, which would otherwise claim the outer loop as a generic
reduction; converges with [#564](https://github.com/Hawkynt/Polypiler/issues/564) on the same operation.
