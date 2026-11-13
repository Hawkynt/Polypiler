# O0307 — Replace every value with an affine evolution in a loop by an incrementally updated one

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0307`.

## What it does

`OPT-LOOP-IVSIMPLIFY` handles `scale·i + offset` with constant coefficients of the counter's own width.
Real loops have more: a stride that is a parameter (`i * stride`), a record size that is not a power of two,
a 16-bit counter widened to index a 32-bit offset, a value derived from a value derived from the counter.
PB-Compiler plans this as the general pass — "recognize the algebraic property, not the named shape" — and
names scalar evolution as what it needs; Polypiler has [#350](https://github.com/Hawkynt/Polypiler/issues/350) for exactly that. PRD §4.8, §19.4.

## Example

### Before

```
head:
  %i = Phi.u32 [pre: 0], [body: %i2]
  %c = ICmp.ult.u32 %i, %n
  Br %c, body, exit
body:
  %o = Mul.u32[wrap] %i, %stride        // %stride defined before the loop
  Call.void @use(%o)
  %i2 = Add.u32[wrap] %i, 1
  Br head
```

### After

```
head:
  %i = Phi.u32 [pre: 0], [body: %i2]
  %o = Phi.u32 [pre: 0], [body: %o2]
  %c = ICmp.ult.u32 %i, %n
  Br %c, body, exit
body:
  Call.void @use(%o)
  %o2 = Add.u32[wrap] %o, %stride
  %i2 = Add.u32[wrap] %i, 1
  Br head
```

## When it applies

(1) `EvolutionOf(v, L)` is `AddRec(start, step)` of `L` itself with both components `Invariant`
or affine combinations of invariants; `Unknown` blocks the rewrite. The analysis already encodes the
hard conditions: an extension keeps an `AddRec` only under the matching no-wrap flag, which is set only when
[#94](https://github.com/Hawkynt/Polypiler/issues/94) bounds the recurrence, and truncation is exact modulo the narrower width. (2) Materializing
`start` and `step` in the preheader uses only `Add`, `Sub`, `Mul` and `Shl` with `[wrap]` and conversions —
nothing that can trap, so a zero-trip loop is unaffected. (3) `v`'s own defining operations carry `[wrap]`
([#122](https://github.com/Hawkynt/Polypiler/issues/122)) or are conversions; an operation that may trap or saturate is not replaced. (4) The
operations that become dead include a `Mul` or a `Shl` by a non-constant amount. (5) [#155](https://github.com/Hawkynt/Polypiler/issues/155) prefers
the recurrence — the preheader work is charged once, the saving per iteration with the trip count's range —
and on an `emits: source` profile ([#262](https://github.com/Hawkynt/Polypiler/issues/262)) the rewrite is offered at `--opt speed` only.

## How it is proven

`structural+range-facts`: the evolution states `v = start + k·step (mod 2^w)` in iteration `k`; the phi satisfies the same recurrence by induction. The no-wrap conditions are range facts of the analysis. No solver, no timeout path.

## When it must not fire

- `ZeroExtend.u32` of a `u8` counter stepping by 100: 0, 100, 200, 44 — the widened value is 44, a 32-bit recurrence gives 300.
- `%stride` reloaded from memory inside the loop while the loop stores through a pointer that may alias it: the step is not invariant.
- `Mul.i32[trap Overflow] %i, %stride`: the source traps on overflow; the recurrence wraps silently.
- `Mul.u32[wrap] %i, %i2`: a product of two recurrences is quadratic, not affine.
- A value that is affine only on one path through the body (`if (c) o = i * s; else o = 0`): the phi at the join has no single evolution.

## Targets

As `OPT-LOOP-IVSIMPLIFY`: machine-code and assembly backends, and source targets without an
optimizing compiler (PowerBASIC, tokenized BASIC) at `--opt speed`. On mature source targets the form
`i * stride` is what a reader expects and the target compiler reduces it; they keep it below `--opt speed`.
The cost model is the gate everywhere: with a run-time stride the preheader work is not free, and for a
proven short trip count the multiplication can be cheaper.

## Switching it off

`--disable-opt O0307`; active at `--opt balanced`, `speed`, `size` and `memory` on profiles that are not `emits: source`; at `--opt speed` only on `emits: source` profiles; off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` after [#508](https://github.com/Hawkynt/Polypiler/issues/508) (which hoists the invariant parts) and `OPT-LOOP-IVSIMPLIFY`, before `OPT-LOOP-IVREDUNDANT`; the dead multiplications are collected by [#156](https://github.com/Hawkynt/Polypiler/issues/156).
