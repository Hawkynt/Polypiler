# O0434 — Give a likely trace a single entry by duplicating the blocks behind a side entry

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0434`.

## What it does

A likely path through a function is rarely one block: it is a chain of blocks, and somewhere in the
middle another path joins it. That side entry is what keeps the chain from being merged into one block —
every fact proven along the trace has to hold for the path that joined as well. PB-Compiler plans to
duplicate the blocks a trace is entered through from outside, giving it a single entry (PB O0390, the
classic superblock). `OPT-LAYOUT-TAILDUP` handles the case where every predecessor of a join is an
unconditional jump; this issue handles the rest: the main path keeps the original blocks, the side entry
gets one copy of the trace's remainder. PRD §4.8.

## Example

### Before

```
a:      …  Br b                      // trace: a, b, c
b:      %v = Phi.i16 [a: %x], [rare: %y]
        %w = Add.i16[wrap] %v, 1
        Br c
c:      Call.void @use(%w)
        Ret.void
rare:   …  Br b                      // side entry at b (rare is cold)
```

### After

```
a:      …  Br b
b:      %w = Add.i16[wrap] %x, 1     // only predecessor: a; the phi is gone
        Br c
c:      Call.void @use(%w)
        Ret.void
rare:   …  Br b.side
b.side: %w.side = Add.i16[wrap] %y, 1
        Br c.side
c.side: Call.void @use(%w.side)
        Ret.void
```

## When it applies

(1) `B1 … Bn` is a trace as defined above, computed from `BranchProbability` (`OPT-LAYOUT-PLACEMENT`) in reverse post-order of trace heads, each block in at most one trace. (2) `k` is the largest index with a side entry. (3) No block of `Bk … Bn` is a loop header or an unwind target; all lie in one protected region together with the side-entering blocks ([#115](https://github.com/Hawkynt/Polypiler/issues/115)). (4) Values defined in the suffix are used only inside the suffix or as phi inputs of the suffix's exit successors; each exit successor's phi gets an input per clone. (5) The clone `Bk.side … Bn.side` is built with `BlockDuplicator` of `OPT-LAYOUT-TAILDUP`; phis of `Bk` keep only the side-entry inputs in the clone and only the input from `B(k−1)` in the original, where a single-input phi is then removed. (6) `B(k−1)` ends in `Br Bk`, so that `OPT-LAYOUT-SIMPLIFYCFG` merges the two afterwards — that removed jump is the only saving the cost model can see: `duplicated` = latency 0, size `2 × S`; `kept` = latency `Of(Br).Latency`, size `S + Of(Br).Size`, with `S` the summed size of the suffix; balanced weights, `kept` listed first. (7) The target profile does not declare `emits: source`.

## How it is proven

`structural`: every path executes the same instructions as before — the main path in the original suffix, a side-entering path in an identical copy. No solver, no timeout path.

## When it must not fire

- Retargeting the side entry to the clone but leaving its phi input in the original `Bk`: the original phi names a predecessor that no longer exists.
- A suffix that contains a loop header: the clone's back edge would enter the original loop, which then has two entries.
- Cloning only `Bk` and letting the clone jump back into the original `B(k+1)`: the trace has a side entry again, one block later, and the pass never terminates.
- Assuming the scope gain: a longer straight-line block is only a benefit if a later pass uses it; the opt-report states the modelled saving (one jump) and nothing more.

## Switching it off

`--disable-opt O0434`; active at `--opt balanced` and `speed`, in both cases under the balanced weights; off at `size`, `memory` and `none`.

## Targets

assembly and machine-code backends only; the pass runs only for a target profile that does not declare `emits: source`. A source backend would print the remainder of a function twice, once per path, which no reader wants and a mature target compiler does itself where it pays. Real-mode x86 through [#1046](https://github.com/Hawkynt/Polypiler/issues/1046) is excluded.

## Interactions with other passes

runs in phase `optimize` after `OPT-LAYOUT-TAILDUP` and before the iteration's last `OPT-LAYOUT-SIMPLIFYCFG` (which merges `B(k−1)` with `Bk`), [#158](https://github.com/Hawkynt/Polypiler/issues/158) and [#164](https://github.com/Hawkynt/Polypiler/issues/164) (which now see the phi-free value). A clone is itself a trace and is not processed again in the same phase iteration. Invalidates the CFG.
