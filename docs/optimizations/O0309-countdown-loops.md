# O0309 — Count fixed-trip loops down to zero when the body never reads the counter

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0309`.

## What it does

A loop that only needs to run `n` times does not need to compare its counter with a limit: counting down to
zero lets the decrement set the flag the branch tests — `DEC`/`JNZ` on x86, `DEX`/`BNE` on a 6502, a single
`DJNZ` on a Z80 — and frees the register or memory cell that held the limit. PB-Compiler plans the rewrite
for loops whose counter the body does not read; after `OPT-LOOP-ADDRIV` and `OPT-LOOP-IVREDUNDANT` that
is most array loops. PRD §4.8, §23.5.

## Example

### Before

```
pre:
  Br body
body:
  %i  = Phi.u32 [pre: 1], [body: %i2]
  %s  = Phi.u32 [pre: 0], [body: %s2]
  %s2 = Add.u32[wrap] %s, 7
  %i2 = Add.u32[wrap] %i, 1
  %c  = ICmp.ule.u32 %i2, 100
  Br %c, body, exit
```

### After

```
pre:
  Br body
body:
  %k  = Phi.u32 [pre: 100], [body: %k2]
  %s  = Phi.u32 [pre: 0], [body: %s2]
  %s2 = Add.u32[wrap] %s, 7
  %k2 = Sub.u32[wrap] %k, 1
  %c  = ICmp.ne.u32 %k2, 0
  Br %c, body, exit
```

## When it applies

(1) The loop is bottom-tested with one latch, the latch's conditional branch is its only exit,
and the body executes at least once whenever the preheader is reached. (2) `TripCountOf` is `Exact(n)` with
`1 ≤ n ≤ 2^w − 1` for the counter's width `w`, or `Symbolic(v, offset)` whose value is available in the
preheader and proven in the same range; the trip count of [#350](https://github.com/Hawkynt/Polypiler/issues/350) already accounts for wrap-around — a
loop whose counter wraps before the bound is reached has no finite count and is not rewritten. (3) The
counter phi and its increment have no users other than each other and the exit comparison. (4) A use of the
old counter after the loop is replaced by `start + trips·step` computed with `[wrap]` arithmetic at the
exit. (5) The profile is not `emits: source` ([#262](https://github.com/Hawkynt/Polypiler/issues/262)).

## How it is proven

`structural+range-facts`: a counter from `n` down by one reaches zero after exactly `n` decrements; the trip count is the analysis' fact. No solver, no timeout path.

## When it must not fire

- `for (i16 i = 1; i <= 32767; i++)`: the increment wraps to −32768 and the loop never ends; a countdown from 32767 would end.
- `for (i = 0; i < n; i++) a[i] = 0`: the body reads the counter; counting down changes which elements are touched unless the subscript is rewritten too.
- A top-tested loop with `n = 0` counted down from 0: the decrement wraps to 4294967295 and the body runs 2^32 times.
- The counter is printed after the loop: it must be 101, not 0.
- A second exit (`break`) in the body: the number of iterations is not the trip count.

## Targets

Machine-code and assembly backends only — every ISA Polypiler targets tests for zero more cheaply
than it compares with a limit, and several have a fused instruction (Z80 `DJNZ`, x86 `LOOP`, 6502
`DEX`/`BNE`, ARM `SUBS`/`BNE`). Source-emitting targets are excluded: a descending loop the author wrote
ascending is less readable, their compilers pick the direction themselves, and in BASIC `FOR … STEP -1` is
no faster. [#155](https://github.com/Hawkynt/Polypiler/issues/155) ranks `countdown` (`Sub` + `ICmp.ne` against 0, one live value) before `ascending`
(`Add` + `ICmp` against the bound, two live values) so that equal scores select the countdown; a profile
that prices the zero test higher keeps the ascending loop.

## Switching it off

`--disable-opt O0309`; active at `--opt balanced`, `speed`, `size` and `memory` on profiles that are not `emits: source`; off at `--opt none`. On `emits: source` profiles the pass is not registered.

## Interactions with other passes

runs in phase `specialize` after `OPT-LOOP-ROTATE`, `OPT-LOOP-ADDRIV` and `OPT-LOOP-IVREDUNDANT` (which remove the body's reads of the counter) and after [#510](https://github.com/Hawkynt/Polypiler/issues/510) and [#513](https://github.com/Hawkynt/Polypiler/issues/513), which need the ascending counter.
