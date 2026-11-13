# O0005 — Remove instructions whose results and effects are unobservable

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#156](https://github.com/Hawkynt/Polypiler/issues/156). Once implemented it is switched off with `--disable-opt O0005`.

## What it does

Every other pass leaves debris: the operands of a folded expression, the copy GVN replaced, the temporaries of
a recognized idiom. Unremoved, they are emitted, compiled and executed. DCE is also the pass most easily made
wrong — the difference between "unused" and "unobservable" is exactly the set of effects PRD §17.1 lists.
PRD §4.8 (DCE), §8 P1, §17.5 (stores to unknown memory, volatile reads).

## Example

### Before

```
%t = Mul.u32[wrap] %x, %x        // unused
%u = Add.u32[wrap] %t, 1         // unused
%d = UDiv.u32 %x, %y             // unused, traps when %y = 0
%v = Load.u8[volatile] 0xD012    // unused, device read
Ret.u32 %x
```

### After

```
%d = UDiv.u32 %x, %y
%v = Load.u8[volatile] 0xD012
Ret.u32 %x
```

## When it applies

An instruction is deleted iff it is not marked. Marked initially: every terminator; every instruction whose
[#114](https://github.com/Hawkynt/Polypiler/issues/114) set contains `Memory.Write`, `Memory.Unknown`, `Memory.ExecutableWrite`, `Device.*`,
`External.Call`, `Synchronize`, `Runtime.State`, `Timing`, `Stack`, or a volatile `Memory.Read`; every
instruction with a `Trap`/`Exception` effect ([#115](https://github.com/Hawkynt/Polypiler/issues/115)) unless a fact on the instruction proves the
condition false (divisor non-zero, no overflow in range, index in bounds, pointer non-null). Then every
operand of a marked instruction is marked, transitively. A non-volatile load without a trap possibility is
pure and removable.

## How it is proven

`structural+effect-facts`: an unmarked instruction has no path to an observable effect or to the returned value. No solver, no timeout path.

## When it must not fire

- Unused `UDiv.u32 %x, %y` with `y` possibly 0 — removing it removes a division-by-zero trap.
- Unused `Load.u8[volatile] 0xD012` (C64 raster register) — the read itself is a device interaction.
- Unused result of `Call.u32 @send(%p)` — the call has `External.Call`.
- Unused `Load.u32 %p` where `%p` may be null and the profile traps on null: the trap is observable.
- `while (i != 0) {}` with no effect: deleting the loop turns a hang into a return.

## Switching it off

`--disable-opt O0005`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs last in each iteration of phase `optimize` and once in `legalize` after helper expansion; enabled by every other pass. Preserves the CFG and all CFG-based analyses.
