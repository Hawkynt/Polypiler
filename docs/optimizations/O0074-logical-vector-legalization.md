# O0074 — Legalize logical vectors of 16 to 4096 bits to native SIMD, polyfill, SWAR or scalar code

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#499](https://github.com/Hawkynt/Polypiler/issues/499). Once implemented it is switched off with `--disable-opt O0074`.

## What it does

The vector abstraction must hide "what is a CPU extension and what is just clever register usage":
`Vector<16>`, `Vector<32>`, `Vector<64>` share one interface with `Vector<128>`, `Vector<256>`,
`Vector<512>` and on up to 4096 bits, and the emitter is free to choose — native intrinsics on .NET 10, a
backport polyfill on .NET Framework 4.6, pure software on .NET 2.0, "emitted along the way". This rule is
that choice, made once in the legalizer from the capability graph and the cost model so that every backend
inherits it. PRD §4.7, §8 P3 (SIMD vectorization/scalarization), §8 P5 (logical widths 16 … 4096 with
codegen fallback).

## Example

### Before

```
%r = Xor.vec<32 x u8> %a, %b                    // a 256-bit logical vector
```

### After

```
// profile simd256:  %r = Xor.vec<32 x u8> %a, %b                              (native)
// profile simd128:  %r.0 = Xor.vec<16 x u8> %a.0, %b.0
//                   %r.1 = Xor.vec<16 x u8> %a.1, %b.1                        (split 2 x 128)
// profile swar64:   %r.0 .. %r.3 = Xor.u64 %a.k, %b.k   for k = 0 .. 3       (split 4 x 64, swar)
// profile scalar8:  loop k = 0 .. 31:  %r[k] = Xor.u8 %a[k], %b[k]            (scalar)
```

## When it applies

Every alternative must implement the lane-wise definition of [#381](https://github.com/Hawkynt/Polypiler/issues/381) exactly. `native`: the profile
lists the operation for that width and lane type; saturating, comparison-mask and shift-count semantics
must be the listed operation's documented semantics, not merely "an instruction with a similar name".
`polyfill`: the profile names the library and declares it exact for the operation; the library becomes a
recorded dependency ([#260](https://github.com/Hawkynt/Polypiler/issues/260) manifest, `Runtime.External`). `split` into parts of `p` lanes: legal
for lane-wise operations unconditionally (lane `i` lives in part `i / p`, position `i mod p`); for a
horizontal reduction the parts are reduced and combined in the order the operation specifies — for
floating-point lanes ([#116](https://github.com/Hawkynt/Polypiler/issues/116)) only a split that reproduces that exact association is allowed,
otherwise the reduction goes to `scalar`; for shuffles, extracts and inserts with constant indices, each
result lane is taken from its source part; variable shuffles across parts go to `scalar`. `swar` and
`scalar` inherit the conditions of [#511](https://github.com/Hawkynt/Polypiler/issues/511) and [#509](https://github.com/Hawkynt/Polypiler/issues/509). Lane order in memory is ascending
address = ascending lane index on every path. A vector type whose lane type no alternative supports is
PLY0552. Only `vec` types are handled: integers and bit vectors of the same width are different types
(PRD §4.7).

## How it is proven

`structural+exhaustive-16bit`: splitting a lane-wise operation is re-bracketing of independent lanes; the non-lane-wise rules are checked per operation by the cross-path measurement; SWAR and scalar parts carry the proofs of O0019 and O0021. No solver, no timeout path.

## When it must not fire

- Splitting a sequential `f64` reduction of `[1e16, 1.0, -1e16, 1.0]` into two halves reduced separately: `(1e16 + 1.0) + (−1e16 + 1.0) = 0.0`; the definition gives 1.0.
- A cross-part shuffle applied per part: reversing a 32-lane vector by reversing each 16-lane half leaves the halves in the wrong order.
- Mapping `Add.vec<32 x u8>[sat]` to a native wrapping add because the profile lists "add".
- Treating `vec<64 x u8>` addition as a 512-bit integer addition: carries cross lanes.
- A polyfill whose `Shl` masks the count while the logical operation defines counts ≥ lane width as zero.

## Switching it off

`--disable-opt O0074`; `Kind = Required`: the rule runs at every `--opt` level including `none`. With it disabled the legalizer has no alternative for these operations on targets that need it and reports PLY0530.

## Interactions with other passes

runs in phase `legalize` under the driver of [#259](https://github.com/Hawkynt/Polypiler/issues/259); invokes [#511](https://github.com/Hawkynt/Polypiler/issues/511) and [#509](https://github.com/Hawkynt/Polypiler/issues/509) as sub-rules; [#498](https://github.com/Hawkynt/Polypiler/issues/498) handles overlays of vectors; afterwards [#158](https://github.com/Hawkynt/Polypiler/issues/158) and [#156](https://github.com/Hawkynt/Polypiler/issues/156) clean up part bookkeeping.
