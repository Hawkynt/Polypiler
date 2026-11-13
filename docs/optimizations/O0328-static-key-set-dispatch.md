# O0328 — Turn the linear search of a small constant key set into a multi-way dispatch

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0328`.

## What it does

`for i: if keys(i) = k then exit for` over five constants is a `switch` written as a loop: the keys are
known, each has one answer — its index — and everything else is a miss. As a `Switch`, every backend can
dispatch it the way it dispatches any multi-way branch: a jump table, a compare tree, a perfect hash, a
source-level `switch`/`SELECT CASE`. PB-Compiler implements exactly this canonicalization
(`StaticSearchRecognition` → `IrSwitch`) and leaves the choice of dispatch to its switch lowering.
PRD §4.8, §19.3.

## Example

### Before

```
// global @keys: u8[5] = [17, 2, 91, 4, 33] [readonly]
%f = Memory.Find.u8 @keys, %k, 5
```

### After

```
  Switch.u8 %k, miss, [17: h0], [2: h1], [91: h2], [4: h3], [33: h4]
h0:   Br join
h1:   Br join
h2:   Br join
h3:   Br join
h4:   Br join
miss: Br join
join:
  %f = Phi.u32 [h0: 0], [h1: 1], [h2: 2], [h3: 3], [h4: 4], [miss: 5]
```

## When it applies

(1) The searched storage is a module-level `[readonly]` global with a complete constant
initializer ([#121](https://github.com/Hawkynt/Polypiler/issues/121)), searched from its first element; `n` is a constant, `4 ≤ n ≤ 64`, not above
the length. (2) The case values are the distinct values among entries `0 … n−1`, each of type `T` — the
type of the key, fixed by `Memory.Find.<T>` ([#189](https://github.com/Hawkynt/Polypiler/issues/189)) — and each case yields the smallest index
holding that value; the default yields `n`. (3) The read contract of the original is irrelevant: the table
is constant and wholly readable, so `[read=prefix]` and `[read=full]` behave alike. (4) [#155](https://github.com/Hawkynt/Polypiler/issues/155)
prefers the dispatch to `LoopCost` of the scan at its mean of `n/2` iterations.

## How it is proven

`structural`: a finite map from key value to first index, enumerated from the constant initializer; equality on `T` is bit equality for integers. No solver, no timeout path.

## When it must not fire

- Repeated key `[7, 9, 7, 3]`: the search returns 0 for 7; a case list built last-wins returns 2.
- A `u8` table searched with a key that the source compared after sign extension: 200 and −56 are the same bit pattern; the case value must be taken in the element type, once.
- A table without `[readonly]`: a store elsewhere changes the key set after the switch was built.
- `n` smaller than the table: entries from index `n` on must not become cases.
- A miss mapped to −1 because the source loop did so: the operation's miss result is `n`; the source's failure value is applied by the `Select` `OPT-LOOP-FIND` left behind.

## Targets

Every target. In generated source a `switch` over five constants is the idiomatic form and reads
better than a call to a search routine; machine-code backends dispatch it with whatever their switch
lowering prefers for the key set. The dispatch costs code — one case per key — where the scan costs time;
[#155](https://github.com/Hawkynt/Polypiler/issues/155) decides, and under `size` and `memory` it normally keeps `Memory.Find` except for the smallest
sets.

## Switching it off

`--disable-opt O0328`; active at `--opt balanced` and `speed`, and at `size` and `memory` when the cost model approves; off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` after `OPT-LOOP-FIND` (phase `recognize`) and before `OPT-LOOP-BINSEARCH`, which handles the tables this pass leaves; [#164](https://github.com/Hawkynt/Polypiler/issues/164) folds the switch when the key is constant.
