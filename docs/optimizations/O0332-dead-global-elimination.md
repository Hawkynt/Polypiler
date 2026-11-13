# O0332 — Remove globals that are never read, together with the stores into them

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0332`.

## What it does

Translated programs carry write-only state: a debug flag that is set and never tested, a table filled for a
feature the entry points never call. Each costs storage and a store at every assignment, and
[#504](https://github.com/Hawkynt/Polypiler/issues/504) cannot remove the stores because a global outlives the function. [#156](https://github.com/Hawkynt/Polypiler/issues/156) names "unused
functions and globals" as not planned; PB-Compiler's tree-shaker removes such data (upstream O0023).
PRD §4.8 (size and memory objectives).

## Example

### Before

```
global @counter:   i16[1] = [0]
global @debugFlag: i16[1] = [0]
Store.i16 @debugFlag, 1
%c = Load.i16 @counter
%n = Add.i16[wrap] %c, 1
Store.i16 @counter, %n
```

### After

```
global @counter: i16[1] = [0]
%c = Load.i16 @counter
%n = Add.i16[wrap] %c, 1
Store.i16 @counter, %n
```

## When it applies

A global `G` is removed iff (1) it is internal: defined in the module with static lifetime, not reachable under an `export` name ([#125](https://github.com/Hawkynt/Polypiler/issues/125)), not `external`, not a `Device` channel, not volatile ([#121](https://github.com/Hawkynt/Polypiler/issues/121), [#114](https://github.com/Hawkynt/Polypiler/issues/114)), and `ContractOf` reports no layout contract that makes its bytes part of a file or foreign interface ([#76](https://github.com/Hawkynt/Polypiler/issues/76)); (2) *write-only*: every use of `@G` in every function of the module, test functions included, is the address operand of a `Store`, the destination of `Memory.Fill`, `Memory.Copy` or `Memory.Move`, or a `PtrAdd` whose every use satisfies this rule; any `Load`, reading operand, `PtrCmp`, `PtrDiff`, `PtrToInt`, call argument, return, or store *of the address* keeps `G` ([#82](https://github.com/Hawkynt/Polypiler/issues/82): `EscapeOf` must be `NoEscape`); (3) every removed write is proven unable to trap by itself: its destination range is in bounds (`IsInBounds` is `True` or the index is a literal the verifier accepted, [#93](https://github.com/Hawkynt/Polypiler/issues/93)), and for `Memory.Copy`/`Memory.Move` the source range is proven accessible and, for `Copy`, disjoint — otherwise the trap would disappear with the write and `G` is kept; (4) no removed write is atomic or lies under a `Synchronize` effect; (5) the initializer is constant (no code runs to produce it). The operand values of removed writes are not touched: an operand that may trap stays alive under the rules of [#156](https://github.com/Hawkynt/Polypiler/issues/156).

## How it is proven

`structural+alias-facts+effect-facts`: a location that no instruction and no external party can read has no observable content. No solver, no timeout path.

## When it must not fire

- `Store.i16[bounds] (PtrAdd @log, %i), %v` with `@log: i16[16]` and `i = 100`: the store traps `IndexOutOfRange`; removing it removes the trap, so `@log` stays.
- A global exported or declared `external`: another module reads it.
- A write-only hardware latch modelled as a `Device` region: the write *is* the effect.
- `Store.i16 @flag, (SDiv.i16 %a, %b)`: the store goes, the division with `b = 0` must still trap — the value is not deleted here.
- A global whose address is passed to `Call @extern(@buf)`: the callee may read it.
- A global read only by a `test` function: the test would observe the removal.

## Targets and cost gate

Every target, source-language targets included: a removed declaration and removed assignments make generated source shorter and no less readable, and no target compiler can remove a non-static global on its own authority. Every component of `Cost` falls or stays equal, so no alternative is submitted to the cost model.

## Switching it off

`--disable-opt O0332`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` as a module pass after [#159](https://github.com/Hawkynt/Polypiler/issues/159) and before the function passes of each iteration; enables [#156](https://github.com/Hawkynt/Polypiler/issues/156) (stored values), `OPT-MEM-LITPOOL` and `OPT-MEM-CONSTMERGE` (fewer objects); enabled by [#164](https://github.com/Hawkynt/Polypiler/issues/164) removing the branches that held the only reads.
