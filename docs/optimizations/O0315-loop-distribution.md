# O0315 — Split a loop so that a vectorizable statement group gets a loop of its own

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0315`.

## What it does

`c[i] = a[i] + b[i]; log(c[i])` vectorizes not at all: the call blocks [#510](https://github.com/Hawkynt/Polypiler/issues/510) for the whole body.
Split into two loops over the same range, the first is a plain element-wise map. Distribution is the inverse
of `OPT-LOOP-FUSE` and has the mirrored hazard: it runs *all* iterations of the first group before any of
the second, so it is illegal when the first group depends on what the second did in an earlier iteration,
and it moves the second group's effects and traps behind stores they used to precede. PB-Compiler plans it
with exactly these two conditions. PRD §4.8.

## Example

### Before

```
body:                                    // i = 0 .. n-1; %c is a local array that does not escape
  %x = Load.u16[inbounds #1] (ElemAddr.ptr<u16>[inbounds #1] %a, %i)
  %y = Load.u16[inbounds #2] (ElemAddr.ptr<u16>[inbounds #2] %b, %i)
  %z = Add.u16[wrap] %x, %y
  Store.u16[inbounds #3] (ElemAddr.ptr<u16>[inbounds #3] %c, %i), %z
  Call.void @log(%z)
  %i2 = Add.u32[wrap] %i, 1
  ...
```

### After

```
body.1:                                  // i = 0 .. n-1
  %x = Load.u16[inbounds #1] (ElemAddr.ptr<u16>[inbounds #1] %a, %i)
  %y = Load.u16[inbounds #2] (ElemAddr.ptr<u16>[inbounds #2] %b, %i)
  %z = Add.u16[wrap] %x, %y
  Store.u16[inbounds #3] (ElemAddr.ptr<u16>[inbounds #3] %c, %i), %z
  ...
body.2:                                  // k = 0 .. n-1
  %w = Load.u16[inbounds #3] (ElemAddr.ptr<u16>[inbounds #3] %c, %k)
  Call.void @log(%w)
  ...
```

## When it applies

Let `G1` be the group that runs first after the split and `G2` the other. (1) Same-iteration
order: `G1` uses no value defined by `G2`, and `G2`'s uses of `G1`'s values are replaced by loads of the
element `G1` stored them to (same subscript, no other store to that array in the loop); otherwise `scalar
%v crosses the split`. (2) Memory dependences ([#82](https://github.com/Hawkynt/Polypiler/issues/82), `UniformDependence` of
`OPT-LOOP-INTERCHANGE` with depth 1): no access of `G2` in iteration `p` and access of `G1` in a later
iteration `q > p` may touch the same storage with at least one of them a store — the split would run `G1`'s
iteration `q` first. `Unknown` and `MayAlias` block. (3) Calls in the rest group must not read or write the
clean group's arrays ([#80](https://github.com/Hawkynt/Polypiler/issues/80) summary), beyond the re-read of (1). (4) If the rest group may trap,
unwind or not return ([#115](https://github.com/Hawkynt/Polypiler/issues/115)) and runs second, the clean group's stores must go only to storage that
cannot be observed afterwards — allocations that do not escape the function ([#82](https://github.com/Hawkynt/Polypiler/issues/82)); if the rest
group runs first, the clean group must not store to anything the unwinding path can observe either.
(5) Both loops keep the original bounds ([#350](https://github.com/Hawkynt/Polypiler/issues/350) trip count available before the loop), so a counter
read after the loop takes its value from the second loop. (6) The profitability condition in scope holds and
[#155](https://github.com/Hawkynt/Polypiler/issues/155) prefers `BlockedCost` of the clean loop plus `LoopCost` of the rest over `LoopCost` of the
whole.

## How it is proven

`structural+alias-facts+effect-facts`: under (1)–(4) every pair of dependent or observable actions keeps its relative order, or one of the two is unobservable. No solver, no timeout path.

## When it must not fire

- `c[i] = a[i] + b[i]; a[i+1] = c[i] * 2` on `a = [1,1,1,1]`, `b = [0,0,0]`: as written `c = [1,2,4]`; with the first statement in a loop of its own `c = [1,1,1]`. The groups lie on a dependence cycle.
- `c[i] = a[i] + b[i]; log(c)` where `log` prints the whole array `c`: after the split the first call already sees every element.
- `c` is a caller-visible buffer and `log(…)` may throw in iteration 2: the caller finds 3 elements written as written and all `n` after the split.
- `t = a[i] * 3; log(t)` with `t` never stored: there is nothing to re-read the value from.
- `s += a[i]; log(s)`: the clean candidate carries a dependence (the sum) and is not vectorizable; the split would only cost.

## Targets

Only targets on which the separated loop becomes something better: cost profiles with a `simd` or
`swar` entry (x86-64, AArch64, WebAssembly SIMD, C# `Vector<T>`, SWAR on 32/64-bit registers). On targets
without them — 6502, Z80, tokenized BASIC — two loops are strictly worse than one and the pass reports
`target has no vector form`. Source targets with their own vectorizer do not distribute loops at default
levels either, so the rewrite pays there too; the output has two plain loops instead of one, which stays
readable.

## Switching it off

`--disable-opt O0315`; active at `--opt speed` and, when the cost model approves, `balanced`; off at `size`, `memory` and `none`.

## Interactions with other passes

runs in phase `optimize` after [#502](https://github.com/Hawkynt/Polypiler/issues/502) and [#160](https://github.com/Hawkynt/Polypiler/issues/160), immediately before [#510](https://github.com/Hawkynt/Polypiler/issues/510); `OPT-LOOP-FUSE` does not undo it, because it fuses only two clean loops and a distributed pair is never clean on both sides.
