# O0012 — Hoist loop-invariant computations that cannot trap or be invalidated

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#508](https://github.com/Hawkynt/Polypiler/issues/508). Once implemented it is switched off with `--disable-opt O0012`.

## What it does

Inner loops of codecs recompute the same things per element: `width * stride`, `table.length`, a mask built
from a parameter. Hoisting them is the cheapest loop optimization — and the one most easily made wrong,
because moving an instruction before the loop executes it even when the loop body never runs, and before
effects that used to precede it. PRD §4.8 (loop-invariant motion).

## Example

### Before

```
head:
  %i = Phi.u32 [pre: 0], [body: %n]
  %c = ICmp.ult.u32 %i, %len
  Br %c, body, exit
body:
  %k = Mul.u32[wrap] %w, %stride        // invariant
  %a = Add.u32[wrap] %i, %k
  Store.u8 %dst, %a, 0
  %n = Add.u32[wrap] %i, 1
  Br head
```

### After

```
pre:
  %k = Mul.u32[wrap] %w, %stride
  Br head
head: ...
body:
  %a = Add.u32[wrap] %i, %k
  ...
```

## When it applies

Instruction `I` in loop `L` moves to the preheader iff (1) every operand is defined outside `L` or already
hoisted; (2) `I` has no effect other than `Memory.Read` or `Trap` ([#114](https://github.com/Hawkynt/Polypiler/issues/114)); (3) if `I` is a load,
no store, call or unknown-memory write in `L` may alias its location ([#82](https://github.com/Hawkynt/Polypiler/issues/82)), and the load is not
volatile; (4) if `I` may trap ([#115](https://github.com/Hawkynt/Polypiler/issues/115)): either a fact proves it cannot ([#94](https://github.com/Hawkynt/Polypiler/issues/94): divisor
non-zero, no overflow, index in bounds), or `I`'s block dominates all exits of `L`, `I` executes in the
first iteration before any instruction with an observable effect, and the loop is entered only when the
first iteration runs (the preheader is reached only if the header's entry condition is true, i.e. the loop
is in rotated form) — otherwise the trap would be added to a zero-trip execution or reordered before an
effect; (5) [#155](https://github.com/Hawkynt/Polypiler/issues/155) does not report the hoist as a loss under `--opt size` (hoisting never duplicates
code, so this only refuses hoists that lengthen a live range on register-starved profiles).

## How it is proven

`structural+alias-facts+effect-facts` (plus `range-facts` where a trap is excluded): the hoisted instruction computes the same value in every iteration and its execution is unobservable or was guaranteed anyway. No solver, no timeout path.

## When it must not fire

- `for (i = 0; i < n; i++) s += a / b;` with `n = 0` and `b = 0`: the source returns without dividing; hoisting `a / b` traps.
- `for (…) { print(i); x = a / b; }` with `b = 0`: the source prints once before trapping; hoisting traps before the print.
- `for (…) { v = p[0]; q[i] = v + 1; }` with `q` aliasing `p`: `p[0]` changes in iteration 0.
- A load of a volatile status register polled in a loop.
- `%len = Call.u32 @length(%list)` where the body appends to the list.

## Switching it off

`--disable-opt O0012`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` after [#158](https://github.com/Hawkynt/Polypiler/issues/158) and [#160](https://github.com/Hawkynt/Polypiler/issues/160); enables [#502](https://github.com/Hawkynt/Polypiler/issues/502) (hoisted lengths), [#510](https://github.com/Hawkynt/Polypiler/issues/510) and [#513](https://github.com/Hawkynt/Polypiler/issues/513) (smaller bodies).
