# O0446 — Merge identical instruction sequences whenever all their blocks are measured cold

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0446`.

## What it does

`OPT-LAYOUT-TAILMERGE` has to be careful: merging two tails that are not all of a join's
predecessors costs a taken jump, so it asks the cost model, never merges for speed and never merges
partial groups in source targets. For code that does not run, that caution buys nothing — the jump is
never taken, and every duplicated byte of error handling is a byte in the image. PB-Compiler plans to
apply its congruence test by temperature (PB O0391): merge the cold copies always, leave the hot ones
alone. With measured counts "cold" is a fact about the workload, not a guess. PRD §4.8.

## Example

### Before

```
err1:                                   // count 0
  Call.void @print(@msg.range)
  Call.void @abort(%code1)
  Unreachable
err2:                                   // count 0
  Call.void @print(@msg.range)
  Call.void @abort(%code2)
  Unreachable
```

### After

```
err1:
  Br err1.tail
err2:
  Br err1.tail
err1.tail:
  %code = Phi.i16 [err1: %code1], [err2: %code2]
  Call.void @print(@msg.range)
  Call.void @abort(%code)
  Unreachable
```

## When it applies

(1) Every block of the group has `IProfileData.TemperatureOf = Cold`. (2) The group satisfies rules 1 to 4 of `OPT-LAYOUT-TAILMERGE` (congruent terminators, congruent suffix with phis for differing operands, no suffix value used outside except through successor phis, one protected region, no unwind target). (3) The suffix has at least one instruction besides the terminator, or the blocks are congruent as a whole. (4) For a target profile that declares `emits: source` the group must be complete in the sense of that issue's rule 5. (5) No cost query and no objective test: at every optimizing level the group is merged. (6) The tail inherits the profile key of the first group block and is therefore cold itself. **Without a profile** (`--profile-use` absent, or the function stale or unmeasured) the pass does nothing and writes no report entry: every temperature is `Unknown`, and `Unknown` is neither hot nor cold. PRD §4.8: no translation needs a profile. **Determinism** (PRD §13.1): temperatures are integer comparisons on counts read through `IProfileData`, candidates are visited in reverse post-order and groups are formed in that order — the same input, compiler, profile and seed give the same output.

## How it is proven

`structural`, the proof of `OPT-LAYOUT-TAILMERGE`: each path executes the same instructions on the same values in the same order. Temperature decides only whether to merge. No solver, no timeout path.

## When it must not fire

- Merging because the blocks are *probably* cold (they call `@abort`): a validation routine whose "error" arm is its common result would get a jump on its hot path. Only a measured 0 qualifies.
- A group in which one block has count 3: that path would pay the jump on every execution; the group is left to the cost rule.
- Treating `Unknown` as cold: after a stale profile every block is unknown, and the whole function would be merged for size at `--opt speed`.
- Merging two cold sequences that differ in the message they print: the suffix is not congruent at that position; only the common part below it is shared, with a phi if the difference is an operand value.

## Switching it off

`--disable-opt O0446`; active at `--opt balanced`, `speed`, `size` and `memory` when a profile is attached; off at `--opt none`.

## Targets

every target for complete groups; assembly and machine-code backends only for partial groups (rule 4), where the saving matters most — a 6502 or 8086 image with a memory budget. A source target gets the same benefit `OPT-LAYOUT-TAILMERGE` gives it, now also at `--opt speed`. The cost model of [#155](https://github.com/Hawkynt/Polypiler/issues/155) is deliberately not asked: its only argument against merging is the latency of a jump that the measurement says is not executed.

## Interactions with other passes

runs in phase `optimize` directly after `OPT-LAYOUT-TAILMERGE`, on the groups that pass refused for cost or objective; with `OPT-PGO-TAILMERGE` enabled the policy of that issue calls this merge for cold groups. Invalidates the CFG.
