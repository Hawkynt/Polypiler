# O0308 — Express induction variables that advance in lockstep through one of them

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0308`.

## What it does

Hand-written loops keep an index and an offset side by side (`j = j + 1` next to the `for i`), and
`OPT-LOOP-IVSIMPLIFY` and `OPT-LOOP-ADDRIV` create more recurrences of the same kind. Two variables that
start a constant apart and take the same step stay that far apart for ever — in wrapping arithmetic even
across overflow — so one loop-carried value and one addition suffice. On a 6502 or an 8086 that is a register
back. PB-Compiler implements this in `PhiCongruence`. PRD §4.8.

## Example

### Before

```
head:
  %i = Phi.u32 [pre: 0],   [body: %i2]
  %j = Phi.u32 [pre: 100], [body: %j2]
  %c = ICmp.ult.u32 %i, 100
  Br %c, body, exit
body:
  %k = Sub.u32[wrap] %j, 100
  ...                                   // uses %i and %k as subscripts
  %j2 = Add.u32[wrap] %j, 1
  %i2 = Add.u32[wrap] %i, 1
  Br head
```

### After

```
head:
  %i = Phi.u32 [pre: 0], [body: %i2]
  %j = Add.u32[wrap] %i, 100
  %c = ICmp.ult.u32 %i, 100
  Br %c, body, exit
body:
  %k = Sub.u32[wrap] %j, 100            // folds to %i ([#154](https://github.com/Hawkynt/Polypiler/issues/154))
  ...
  %i2 = Add.u32[wrap] %i, 1
  Br head
```

## When it applies

(1) Both values are phis of the same loop header with the same incoming blocks and the same
integer type. (2) [#350](https://github.com/Hawkynt/Polypiler/issues/350) gives both the same `step` — the same constant or the same loop-invariant SSA
value — and loop-invariant starts. (3) Both latch updates carry `[wrap]` ([#122](https://github.com/Hawkynt/Polypiler/issues/122)): the difference
is then invariant modulo `2^w` through any overflow, and no range proof is needed. An update with `[trap]`,
`[sat]` or `[undefined]` is not merged — dropping it would drop its trap or its clamp. (4) `delta` is
computed with `Sub.T[wrap]`, which cannot trap, so the preheader is safe for a zero-trip loop. The
replacement is inserted after the header's phis and therefore dominates every use of the old phi.

## How it is proven

`exhaustive-8bit+structural`: `(a + d) + s ≡ (a + s) + d (mod 2^w)` is a ring identity, checked exhaustively at 8 bit; the invariant holds by induction. No solver, no timeout path.

## When it must not fire

- Different steps (`i += 1`, `j += 2`): the difference grows by one per iteration.
- `j` is updated only under a condition in the body: it is not a recurrence of the loop.
- A `u8` shadow of a `u32` counter: `j` wraps at 256, `i + delta` does not.
- `j2 = Add.u8[sat] %j, 1` next to a wrapping `i`: once `j` clamps at 255 the difference changes.
- `j2 = Add.i32[trap Overflow] %j, 1` starting at 2147483000 next to `i` starting at 0: the source traps in its 648th iteration; without `j` there is no trap.

## Targets

Every target. One loop-carried value fewer is a register saved on machine targets and a variable
removed from generated source, where `b[i]` reads better than `b[j - 100]`. The rewrite removes an addition
from the latch and adds at most one to the header that normally folds away; it never duplicates code, so it
is not cost-gated — [#155](https://github.com/Hawkynt/Polypiler/issues/155) has no cheaper alternative to offer.

## Switching it off

`--disable-opt O0308`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` immediately after `OPT-LOOP-IVSIMPLIFY` and `OPT-LOOP-IVGENERAL`, before [#158](https://github.com/Hawkynt/Polypiler/issues/158); [#154](https://github.com/Hawkynt/Polypiler/issues/154) cancels compensating offsets; enables [#502](https://github.com/Hawkynt/Polypiler/issues/502), [#510](https://github.com/Hawkynt/Polypiler/issues/510) and `OPT-LOOP-COUNTDOWN`.
