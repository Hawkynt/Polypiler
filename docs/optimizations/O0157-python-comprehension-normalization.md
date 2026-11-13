# O0157 — Normalize Python comprehensions and generator pipelines into the shared iteration IR

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#786](https://github.com/Hawkynt/Polypiler/issues/786). Once implemented it is switched off with `--disable-opt O0157`.

## What it does

Python writes element pipelines as comprehensions — `sum(x * 2 for x in xs if x > 6)` — and as the lazy
built-ins `map` and `filter`. A list comprehension is eager; a generator expression is lazy and can be
consumed once; `int` never overflows. Each of these separates Python from the fixed-width eager
`Map`/`Filter`/`Reduce`, and each is a precondition of normalization. PRD §4.5 (comprehensions), §4.6
(arbitrary `int`, iterators), §8 P3 ("port typed comprehensions and sequences") and P4, §17.3. Calls are
matched as Source Semantic IR `SourceCall` nodes carrying the resolved library identity and signature —
never by method name text. The result consists of the sequence operations of [#201](https://github.com/Hawkynt/Polypiler/issues/201) (`SeqOf`,
`Filter.seq`, `Map.seq`, `Reduce[order: left | assoc]`, `Zip`, `Materialize`, `SeqLen`), written as that
issue defines them.

## Example

### Before

```
%g = SourceCall "python:genexpr(for x in ITER if COND)" %xs, λcond(%x): PyCmp.gt %x, 6, λelt(%x): PyMul %x, 2
%s = SourceCall "builtins.sum(iterable, start)" %g, 0
```

### After

```
%s0 = SeqOf %xs
%f = Filter.seq<i64> %s0, λ(%x): ICmp.sgt.i64 %x, 6
%m = Map.seq<i64> %f, λ(%x): Mul.i64[checked] %x, 2
%s = Reduce.i64[order: left] %m, λ(%a, %x): Add.i64[checked] %a, %x, 0
```

## When it applies

(1) the names `sum`, `min`, `max`, `map`, `filter`, `list`, `len` resolve
to the built-ins (not shadowed — a [#253](https://github.com/Hawkynt/Polypiler/issues/253) fact); (2) the element and condition expressions are
pure and do not rebind or mutate the iterable; (3) lazy producers (generator expressions, `map`,
`filter`) have exactly one consumer, which runs immediately — no statement that may mutate the source
or observe an effect lies between creation and consumption — and are never consumed twice; (4) the
iterable is a built-in sequence with proven element type; iteration order is index order; (5) integer
arithmetic: when [#253](https://github.com/Hawkynt/Polypiler/issues/253) proves every intermediate and the final result within the 64-bit range,
the operations are plain fixed-width; when it proves only the elements, the operations are emitted
*checked* (as in the After IR above) with the overflow trap mapped to the frontend's big-integer
fallback or an unsupported-feature diagnostic — never wrapping; (6) `min`/`max` keep `ValueError` on
empty input unless `default` is given; `sum` of an empty iterable is `start`.

## How it is proven

`validated-rule+differential`: each mapping is a registry entry in the sense of PRD
§17.3 (library identity, version range, signature, semantics, throws, effects) with checked
preconditions; purity and single-enumeration are `EffectFact` obligations; equivalence is established
by differential execution against the real runtime on the fixtures and on generated inputs. No SMT
claim is made. A failed, unknown or timed-out obligation (budget 5 s each) leaves the IR unchanged and
reports `not applied: proof timeout` or the failing precondition.

## When it must not fire

- Arbitrary precision: `sum([2**62, 2**62])` is `9223372036854775808`; a wrapping 64-bit `Reduce`
  gives `−9223372036854775808`.
- A generator consumed twice: `g = (x * 2 for x in xs); a = sum(g); b = sum(g)` — `b` is 0 because the
  generator is exhausted.
- Late binding of the source: `g = (x for x in xs); xs.append(100); sum(g)` includes the appended
  element, because the list is iterated at consumption time.
- `map` and `filter` are lazy in Python 3: `m = map(f, xs)` calls `f` zero times until consumed.
- Empty input: `min(x for x in [])` raises `ValueError`.

## Switching it off

`--disable-opt O0157`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` on Source Semantic IR after de-dynamicization facts are
available ([#253](https://github.com/Hawkynt/Polypiler/issues/253)); produces IR equal, under the correspondence that [#195](https://github.com/Hawkynt/Polypiler/issues/195),
[#194](https://github.com/Hawkynt/Polypiler/issues/194) and [#197](https://github.com/Hawkynt/Polypiler/issues/197) state between their span forms and the sequence forms of
[#201](https://github.com/Hawkynt/Polypiler/issues/201), to what the index-loop rules produce for the same computation (modulo the checked
arithmetic where ranges are unproven).
