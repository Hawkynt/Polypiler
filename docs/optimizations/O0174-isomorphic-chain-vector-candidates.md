# O0174 — Form lane-isolated vector candidates from isomorphic independent chains

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#979](https://github.com/Hawkynt/Polypiler/issues/979). Once implemented it is switched off with `--disable-opt O0174`.

## What it does

Once interleaved arithmetic has been sliced into chains, several of them usually do the same thing to
different inputs — four state words of a cipher round, the channels of a pixel. PRD §19.3 says such
chains may be recombined into SIMD or SWAR lanes, and §24 S3 asks for independent-chain vectorization.
The candidate is a vector of the chains' inputs run through one lane-wise copy of the operations. Its
one hard rule is lane isolation: nothing may cross from one lane into the next — which is exactly what a
naive packing of two bytes into one wider scalar violates as soon as an addition carries. The original
instructions are kept as the fallback graph with a `RecoveredFrom` link, and the report says "proven
equivalent to", never "intended as" (PRD §19.1).

## Example

### Before

```
; chains 1..4 from O0173, each:
%t_k = Shl.u32[strict] %x_k, 2
%u_k = Add.u32 %t_k, %x_k
%r_k = Shl.u32[strict] %u_k, 1          ; k = 1..4
```

### After

```
%v = BuildVector.u32x4 %x_1, %x_2, %x_3, %x_4
%t = Shl.u32x4[strict] %v, <2, 2, 2, 2>
%u = Add.u32x4 %t, %v
%r = Shl.u32x4[strict] %u, <1, 1, 1, 1>
%r_k = ExtractLane %r, k-1               ; k = 1..4
```

## When it applies

(1) the chains are independent (no value of one is used in another) and
have the same signature position by position: opcode, type, shift contract, overflow contract,
floating-point format and rounding mode; (2) every operation has a lane-wise vector form in
[#381](https://github.com/Hawkynt/Polypiler/issues/381) whose lane `i` result depends only on lane `i` of its operands — additions wrap per
lane, shifts apply per lane, floating-point operations round per lane; (3) no operation in the chains
can trap, reads flags or has effects; (4) corresponding constants may differ and are collected into a
vector constant; corresponding non-constant leaves become `BuildVector` operands; (5) the group size
is a supported lane count; a group of 3, 5, 6 or 7 chains is split into the largest supported groups
and the rest stay scalar; (6) the scalar chains stay available as fallback, and the rewrite is
recorded with its cost evidence so that [#155](https://github.com/Hawkynt/Polypiler/issues/155) can revert it.

## How it is proven

`smt-bitvector` per lane: lane `i` of every vector result equals the scalar chain `i`
(the obligation is the scalar equality, instantiated per lane, plus the lane-isolation property of
each vector operation, which is part of [#381](https://github.com/Hawkynt/Polypiler/issues/381)'s contract and cited as a fact);
`exhaustive-8bit` on the `u8x2` instance; floating-point lanes by bit-exact differential
interpretation. A failed, unknown or timed-out obligation (budget 5 s each) leaves the IR unchanged
and reports `not applied: proof timeout` or the failing precondition.

## When it must not fire

- SWAR without isolation: two `u8` additions packed into one `u16` addition — `0x00FF + 0x0001 =
  0x0100` carries from the low lane into the high lane; lane-wise the results are `0x00` and `0x00`.
- A right shift on a packed scalar: `0x0180 >> 1 = 0x00C0` shifts the high lane's low bit into the low
  lane.
- Different operations at one position (`Shl` in one chain, `LShr` in the other): not isomorphic.
- A trapping operation: scalar code performs the divisions one after another and stops at the first
  trap; a vector division has no such order.
- Dependent chains: lane 1's input is lane 0's output — a recurrence, not a vector operation.
- Arithmetic and logical right shift mixed by signedness of the lanes: the vector form must take the
  opcode, not the lane type, from each chain.

## Switching it off

`--disable-opt O0174`; active at `--opt balanced` and `speed`; off at `size`, `memory` and
`--opt none`.

## Interactions with other passes

runs in phase `recover` directly after [#981](https://github.com/Hawkynt/Polypiler/issues/981) (O0173); its output is a
candidate that [#155](https://github.com/Hawkynt/Polypiler/issues/155) evaluates per target; enables SIMD selection in the backends and, where a
target has no vectors, is reverted to the fallback.
