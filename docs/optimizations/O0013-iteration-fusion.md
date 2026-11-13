# O0013 — Fuse map/filter/reduce pipelines into one loop without intermediate collections

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#507](https://github.com/Hawkynt/Polypiler/issues/507). Once implemented it is switched off with `--disable-opt O0013`.

## What it does

`data.map(f).filter(g).reduce(add)` in JavaScript, a LINQ `Where/Select/Sum`, a Perl `map`/`grep` chain: the
source allocates one collection per stage and walks the data once per stage. Fused, it is one loop and no
allocation — the difference between usable and unusable on small targets. But eager pipelines run *all* of
`f` before any `g`; interleaving them is only invisible when the stages cannot observe each other. PRD §4.5,
§4.8 (iteration fusion), §6.4 "Sequence fusion", §6.2 "JS array has a getter with side effects".

## Example

### Before

```
%m = Map.seq<u32> %src, @f            // allocates len(%src) elements
%k = Filter.seq<u32> %m, @g           // allocates up to len(%src) elements
%r = Reduce.u32[order=left] %k, @add, 0
```

### After

```
%r = Reduce.u32[order=left] %src, λ(%acc, %x) {
       entry: %y = Call.u32 @f(%x); %c = Call.bool @g(%y); Br %c, keep, skip
       keep:  %n = Call.u32 @add(%acc, %y); Ret.u32 %n
       skip:  Ret.u32 %acc
     }, 0                                // one block-bodied kernel ([#120](https://github.com/Hawkynt/Polypiler/issues/120)); @add runs only for accepted elements
```

## When it applies

(1) Each intermediate sequence has one consumer and does not escape ([#82](https://github.com/Hawkynt/Polypiler/issues/82)). (2) For a lazy source
pipeline no further condition is needed for ordering: the source semantics already interleave. (3) For an
eager pipeline with stages `s1 … sk` (element access counts as stage `s0` when it has effects, e.g. a JS
getter or proxy), fusion changes the call order from "all of `s1`, then all of `s2`" to "per element".
It is legal iff at most one stage has any effect or may trap ([#80](https://github.com/Hawkynt/Polypiler/issues/80) summary incl. `MayTrap`), and
that stage does not read state another stage writes. (4) No stage writes the source sequence or changes its
length; the element count is captured once, as the source language does. (5) Every stage is called exactly
as often and on the same values as before: `Filter` after `Map` evaluates `f` on rejected elements too.
(6) Holes and missing elements are skipped or visited exactly as the source operation specifies
([#201](https://github.com/Hawkynt/Polypiler/issues/201) carries that contract). (7) A reduction's combining function is applied in source order;
no re-association. Exception regions ([#115](https://github.com/Hawkynt/Polypiler/issues/115)) enclosing the pipeline are kept around the loop.

## How it is proven

`effect-facts+alias-facts`: under (3) two stage calls that are swapped by the interleaving commute, so every observable trace is unchanged. No solver, no timeout path.

## When it must not fire

- `[1,2].map(f).filter(g)` where `g` throws on its first element (3) and `f` throws on its second (2): eagerly `f(2)` throws first; fused, `g(3)` throws first — a different exception.
- `f` logs and `g` logs: eagerly the log is `f f f g g g`, fused `f g f g f g`.
- A JS array whose index 1 is a getter with a side effect: eagerly all element reads precede every `g` call (PRD §6.2).
- `f` appends to the source array: the eager `map` uses the length captured at its start.
- `data.map(f)` stored in a variable that is used twice: the intermediate collection is observable through its second consumer.

## Switching it off

`--disable-opt O0013`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` after [#160](https://github.com/Hawkynt/Polypiler/issues/160) and [#159](https://github.com/Hawkynt/Polypiler/issues/159) (small stage functions are inlined into the fused body afterwards); enables [#500](https://github.com/Hawkynt/Polypiler/issues/500), [#502](https://github.com/Hawkynt/Polypiler/issues/502), [#510](https://github.com/Hawkynt/Polypiler/issues/510) and [#959](https://github.com/Hawkynt/Polypiler/issues/959).
