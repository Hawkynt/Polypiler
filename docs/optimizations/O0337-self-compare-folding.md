# O0337 — Fold a block comparison of a location with itself to its constant result

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0337`.

## What it does

Where `OPT-MEM-SELFCOPY` removes `rec = rec` as a statement, the same designators appear as an
expression: `IF rec = rec`, or after inlining `equals(a, a)`. A byte comparison of a block with itself
is always equal — unlike a floating-point comparison it does not care about NaN — so the whole
`Memory.Equal` folds to `true`. PB-Compiler documents it as UDT self-compare folding (upstream O0216,
split from O0015 together with O0215).

## Example

### Before

```
%eq = Memory.Equal.u8 %r, %r, 6
Br %eq, same, differ
```

### After

```
Br true, same, differ          // folded further by OPT-SCCP
```

## When it applies

(1) `SameLocation.Prove(a, b, n × size, point)` is `True` (`OPT-MEM-SELFCOPY`, on [#82](https://github.com/Hawkynt/Polypiler/issues/82): same SSA value or `MustAlias`); (2) the comparison cannot trap: `n` is the literal 0, or `IsInBounds(a, n × size)` is `True` and the provenance is not `MayBeNull` ([#93](https://github.com/Hawkynt/Polypiler/issues/93)) — under `[read=full]` the whole range is checked before reading, and under `[read=prefix]` a self-comparison never finds a deciding element and therefore reads all `n`, so in both cases an inaccessible range traps and the fold would hide it; (3) the region is not accessed atomically and no `Synchronize` effect covers it ([#114](https://github.com/Hawkynt/Polypiler/issues/114)); volatile regions cannot occur, the operations reject them (`PLY0258`); (4) the result type is the operation's: `bool` for `Equal`, `i32` for `Compare`, the type of `%n` for `Mismatch`.

## How it is proven

`alias-facts+range-facts`: for every memory state `m` and every `i < n`, `m[a + i] = m[a + i]`. No solver, no timeout path.

## When it must not fire

- The value comparison `FCmp.oeq.f64 %x, %x` is false for NaN; a record with an `f64` field holding NaN is still *bitwise* equal to itself, which is why only `Memory.*` is folded.
- `Memory.Equal.u8 %p, %p, %n` with `n = 8` on a 6-byte region: the comparison traps `IndexOutOfRange`; folding to `true` hides the trap.
- `a[i] = a[j]` compared element-wise with `i`, `j` possibly equal: not a self-comparison.
- A block another thread updates under a lock: two reads of the same byte may differ; such regions are excluded.

## Targets and cost gate

Every target. The fold replaces an operation by a constant and usually removes a branch after it; every cost component falls, so the cost model is not consulted. Generated source loses a `memcmp(a, a, n)` that no target compiler folds unless it can prove both pointers equal.

## Switching it off

`--disable-opt O0337`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` after [#159](https://github.com/Hawkynt/Polypiler/issues/159), [#158](https://github.com/Hawkynt/Polypiler/issues/158) and `OPT-MEM-SELFCOPY`; enables [#164](https://github.com/Hawkynt/Polypiler/issues/164) (the decided branch) and [#156](https://github.com/Hawkynt/Polypiler/issues/156).
