# O0061 — Specialize JavaScript arrays and typed arrays to typed sequences

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#254](https://github.com/Hawkynt/Polypiler/issues/254). Once implemented it is switched off with `--disable-opt O0061`.

## What it does

A JavaScript `Array` is an object with integer-like keys: it can have holes, mixed element types, getters on
indices, a prototype that answers for missing elements, even a `Proxy` in front of it. A `Uint8Array` is
none of that — but reads past its end return `undefined` instead of failing and stores coerce silently.
Typed targets need a real array; getting there means proving which of these behaviours cannot occur and
reproducing the ones that can. PRD §4.6 (holes, getters, proxies), §6.2 scenarios 1 and 2, §8 P2 ("proven
typed arrays").

## Example

### Before

```
// fact: %data : FixedSequence(u8, n) [Proven]; %i in [0, n-1]
%e = Dyn.GetIndex %data, %i
%x = Dyn.BitXor %e, 90
%m = Dyn.BitAnd %x, 255
Dyn.SetIndex %out, %i, %m
```

### After

```
%e = Load.u8[inbounds #1] %data, %i
%m = Xor.u8 %e, 90
Store.u8[inbounds #2] %out, %i, %m
```

## When it applies

(1) The sequence's fact from [#253](https://github.com/Hawkynt/Polypiler/issues/253) is a typed array, or an `Array` proven *dense* (created by a
literal without elisions, by `new Array(n).fill(v)`, `Array.from`, or by `push` only), *homogeneous* (every
stored value has the element representation), *accessor-free* (no `Object.defineProperty`/`defineProperties`
on it and it does not escape to code that could add one) and not a `Proxy`. An array that escapes to an
unknown call loses density and accessor-freedom ([#114](https://github.com/Hawkynt/Polypiler/issues/114) `Memory.Unknown`). (2) A read is a direct
load only when [#94](https://github.com/Hawkynt/Polypiler/issues/94) proves `0 ≤ i < length` and `i` is an integer; otherwise the result includes
`undefined`. (3) Typed-array stores convert the value as the element type prescribes: `ToUint8` = modulo
256 after truncation, `Uint8ClampedArray` = round-half-to-even then clamp to [0, 255], NaN → 0. (4) The
length of a typed array is fixed; the length of an `Array` is re-read wherever the source reads it, unless
no instruction between can change it. (5) Element access with possible effects (accessor, proxy) is left as
the dynamic operation, with its effect, in its original position and count.

## How it is proven

`range-facts+effect-facts`: under (1) and (2) the exotic-object lookup reduces to an own data property that exists, i.e. a memory load; validated against `node`. No solver, no timeout path.

## When it must not fire

- `[1, , 3]` summed with `(s + a[i]) | 0`: the hole reads `undefined`, `1 + undefined` is NaN, `NaN | 0` is 0, then `0 + 3` — the result is 3, not 4.
- `new Uint8Array([1, 2, 3])[5]` is `undefined`, not an exception and not 0; `a[5] = 1` does nothing.
- `u8[0] = 300` stores 44, `u8[1] = -1` stores 255, `u8[2] = 1.9` stores 1; `Uint8ClampedArray` stores 255 for 300.
- An array with `Object.defineProperty(a, 1, { get() { log.push('get'); return 2; } })`: each read calls the getter; a buffer load skips the log (PRD §6.2).
- `[1, 'a', 3]`: not homogeneous; typing it as integers changes `+` from concatenation to addition.
- `a.length = 0` inside the loop, or a callee that pushes: a cached length is stale.

## Switching it off

`--disable-opt O0061`; `Kind = Required`, so it runs at every level including `--opt none`. With it disabled, element accesses stay dynamic and a typed target reports PLY0500.

## Interactions with other passes

runs in phase `specialize` after [#253](https://github.com/Hawkynt/Polypiler/issues/253) and together with [#255](https://github.com/Hawkynt/Polypiler/issues/255); enables [#502](https://github.com/Hawkynt/Polypiler/issues/502), [#507](https://github.com/Hawkynt/Polypiler/issues/507), [#510](https://github.com/Hawkynt/Polypiler/issues/510) on JavaScript sources.
