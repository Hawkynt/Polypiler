# O0155 — Normalize JavaScript array methods into the shared iteration IR

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#784](https://github.com/Hawkynt/Polypiler/issues/784). Once implemented it is switched off with `--disable-opt O0155`.

## What it does

JavaScript pipelines are `array.filter(...).map(...).reduce(...)` — Cipher's MAYO test setup filters and
maps its vectors that way. The methods look like the textbook operations and differ from them in ways
that matter: the callback receives the index and the array as extra arguments, holes are skipped, a
typed array's `map` truncates to the element type, and `reduce` without an initial value throws on an
empty array. Normalization therefore depends on the de-dynamicization facts for the receiver and the
callback. PRD §4.5 (JS `map/filter/reduce`), §4.6, §8 P4, §17.3. Calls are matched as Source Semantic IR
`SourceCall` nodes carrying the resolved library identity and signature — never by method name text. The
result consists of the sequence operations of [#201](https://github.com/Hawkynt/Polypiler/issues/201) (`SeqOf`, `Filter.seq`, `Map.seq`,
`Reduce[order: left | assoc]`, `Zip`, `Materialize`, `SeqLen`), written as that issue defines them.

## Example

### Before

```
%q1 = SourceCall "Int32Array.prototype.filter(callbackfn)" %xs, λ(%x, %i, %arr): ICmp.sgt.i32 %x, 6
%q2 = SourceCall "Int32Array.prototype.map(callbackfn)" %q1, λ(%x, %i, %arr): Mul.i32 %x, 2
%s  = SourceCall "Int32Array.prototype.reduce(callbackfn, initialValue)" %q2, λ(%a, %x, %i, %arr): Add.f64 (SIToFP %a), (SIToFP %x), 0
```

### After

```
%s0 = SeqOf %xs
%f = Filter.seq<i32> %s0, λ(%x): ICmp.sgt.i32 %x, 6
%m = Map.seq<i32> %f, λ(%x): Mul.i32 %x, 2
%s = Reduce.f64[order: left] %m, λ(%a, %x): FAdd.f64 %a, (SIToFP %x), 0.0
```

## When it applies

(1) the receiver's method resolves to the built-in (no shadowing own
property, no patched prototype in the analyzed program — a [#253](https://github.com/Hawkynt/Polypiler/issues/253) fact); (2) the receiver is
dense and homogeneous (`Proven` or `Contextual`), so no hole is skipped and no getter runs; (3) the
callback is a resolved function of known arity, pure ([#80](https://github.com/Hawkynt/Polypiler/issues/80)) and does not modify the
receiver; parameters it does not declare are simply not passed, parameters it declares are supplied
(index, array); (4) the arithmetic inside the callback keeps JavaScript semantics as lifted by the
frontend — `x * 2` on numbers is a double multiplication unless de-dynamicization proved an integer
range, `a + x` is `FAdd.f64` unless proven integral within ±2^53; (5) typed-array `map` converts each
result to the element type (modulo 2^8 for `Uint8Array`, clamped for `Uint8ClampedArray`); (6)
`reduce` without an initial value keeps the empty-array `TypeError`.

## How it is proven

`validated-rule+differential`: each mapping is a registry entry in the sense of PRD
§17.3 (library identity, version range, signature, semantics, throws, effects) with checked
preconditions; purity and single-enumeration are `EffectFact` obligations; equivalence is established
by differential execution against the real runtime on the fixtures and on generated inputs. No SMT
claim is made. A failed, unknown or timed-out obligation (budget 5 s each) leaves the IR unchanged and
reports `not applied: proof timeout` or the failing precondition.

## When it must not fire

- Extra callback arguments: `['1','2','3'].map(parseInt)` calls `parseInt('1', 0)`, `parseInt('2',
  1)`, `parseInt('3', 2)` and yields `[1, NaN, NaN]`; a one-argument `Map` yields `[1, 2, 3]`.
- Typed-array truncation: `new Uint8Array([200, 100]).map(x => x * 2)` is `[144, 200]`; the same on a
  plain array is `[400, 200]`.
- Holes: `[1, , 3].map(x => x * 2)` is `[2, <hole>, 6]` with length 3; the callback runs twice. A
  dense `Map` runs it three times on `undefined`.
- `reduce` on an empty array without an initial value throws `TypeError`.
- A callback that pushes to the receiver: the elements appended during the call are not visited by
  `map`, but they exist afterwards — an effect a pure `Map` does not have.

## Switching it off

`--disable-opt O0155`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` on Source Semantic IR after de-dynamicization facts are
available ([#253](https://github.com/Hawkynt/Polypiler/issues/253)); produces IR equal, under the correspondence that [#195](https://github.com/Hawkynt/Polypiler/issues/195),
[#194](https://github.com/Hawkynt/Polypiler/issues/194) and [#197](https://github.com/Hawkynt/Polypiler/issues/197) state between their span forms and the sequence forms of
[#201](https://github.com/Hawkynt/Polypiler/issues/201), to what the index-loop rules produce for the same computation; enables fusion and
typed lowering.
