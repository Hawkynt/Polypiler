# O0427 — Infer likely edges statically and place the common path as one fall-through sequence

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0427`.

## What it does

Blocks emitted in source order put an argument check's error message between the test and the work
it guards, and a loop's rare exit path in the middle of its body. The instructions that actually run are
then separated by bytes that almost never do: a taken branch per call on the common path, and on an 8086
or 6502 a prefetch refill with it. Which edge is the common one can be inferred without running the
program — back edges are taken, error and early-return arms are not. PB-Compiler plans the inference and
the placement as PB O0104. This issue delivers both: a static branch-likelihood analysis that the other
layout issues share, and the order that makes likely edges fall through. PRD §4.8, §21.3.

## Example

### Before

```
order: entry, bad, work, done
entry:  %neg = ICmp.slt.i16 %n, 0
        CondBr %neg, bad, work
bad:    Call.void @print(@msg)          // early return
        Ret.void
work:   …
        Br done
done:   Ret.void
```

### After

```
order: entry, work, done, bad
entry → bad: Unlikely      entry → work: Likely
```

## When it applies

**Edge classes**, by the first rule that applies to a two-way branch `B → {S1, S2}`: (R1) exactly one successor is *cold* → that edge is `Unlikely`; a block is cold if it ends in `Unreachable`, if it unconditionally raises (`Throw` or an always-trapping instruction of [#115](https://github.com/Hawkynt/Polypiler/issues/115)) or if all its successors are cold; (R2) one edge is a loop back edge or stays in the innermost loop of `B` while the other leaves it → the leaving edge is `Unlikely`; (R3) exactly one successor ends in `Ret` → that edge is `Unlikely`; (R4) the condition is `ICmp.eq` of a value with the constant 0 or a null pointer → the true edge is `Unlikely`, for `ICmp.ne` the false edge; (R5) otherwise both are `Even`. The sibling of an `Unlikely` edge is `Likely`. Edges of a `Switch` are `Unlikely` to cold successors and `Even` otherwise; an unconditional edge is `Likely`. **Order**: place the entry; after placing `B`, continue with its unplaced `Likely` successor, else its first unplaced `Even` successor in canonical successor order (true before false, `Switch` targets by ascending smallest case value, default last); `Unlikely` successors are appended to a queue. When the chain ends, continue with the oldest queued block that is unplaced and not cold, then with the first unplaced non-cold block in reverse post-order; cold blocks follow last, in reverse post-order. Every reachable block is placed exactly once and the entry stays first, so any result is a legal `Order`; the plans are rebuilt by `BlockLayout.WithOrder`.

## How it is proven

`structural`: a block order with the entry first, executed by the plans of `OPT-LAYOUT-JUMPNEXT`, is behaviourally the function. No solver, no timeout path.

## When it must not fire

- Moving the entry block: the function's symbol labels its first byte.
- Deciding likelihood from block *size* ("the short arm is the error arm"): a one-line fast path is then laid out as cold; upstream excludes this explicitly.
- Iterating successors in hash order: the same module laid out twice gives two different images (PRD §13.1).
- Treating "no rule applies" as `Unlikely`: both arms of an ordinary `if` would be deferred and the fall-through lost.
- Classifying a block as cold because it calls a function named like an error routine: names carry no semantics here; only `Unreachable`, raising and the propagation rule count.

## Switching it off

`--disable-opt O0427`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none` (identity order).

## Targets

assembly and machine-code backends only — the x86 NASM backend ([#931](https://github.com/Hawkynt/Polypiler/issues/931)) and the 6502 selector ([#1025](https://github.com/Hawkynt/Polypiler/issues/1025)) in this issue's tests, the other `ISA-*` emitters as they adopt `BlockLayout`. A source backend prints the region tree of [#262](https://github.com/Hawkynt/Polypiler/issues/262) and never sees a block order, so the pass does not run for a profile that declares `emits: source`; nothing in generated source changes. Real-mode x86 through [#1046](https://github.com/Hawkynt/Polypiler/issues/1046) is excluded too: PB-Compiler's own assembler lays out that code. The cost model is not asked: reordering adds and removes no instruction by itself; the gain appears through `OPT-LAYOUT-JUMPNEXT` and `OPT-LAYOUT-INVERT`.

## Interactions with other passes

runs in phase `legalize` after `leg.layout` and before `OPT-LAYOUT-HOTCOLD`, `OPT-LAYOUT-JUMPNEXT` and `OPT-LAYOUT-INVERT`. `BranchProbability` is also read by `OPT-LAYOUT-TAILDUP`, `OPT-LAYOUT-SIDEENTRY` and `OPT-LAYOUT-COLDOUTLINE`. When a profile is attached, `OPT-PGO-CHAIN` replaces the order of the functions it has counts for.
