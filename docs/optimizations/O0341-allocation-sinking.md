# O0341 — Move an allocation onto the only path that uses it

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0341`.

## What it does

An error message buffer allocated at function entry and used only when something fails is paid for on
every call. Moving the allocation, and its release, into the path that uses it makes the common path
free of allocator traffic. [#500](https://github.com/Hawkynt/Polypiler/issues/500) removes an allocation nobody uses; this one is used, just
rarely. PB-Compiler sinks such allocations (upstream O0288, pass `AllocationSinking`). PRD §4.8
(allocation objective).

## Example

### Before

```
entry:
  %m = Alloc.ptr<u8>[heap] 24
  Br %err, report, done
report:
  Call.void @format(%m, %code)          // does not capture %m
  Call.void @print(%m)
  Br done
done:
  Release %m
  Ret.void
```

### After

```
entry:
  Br %err, report, done
report:
  %m = Alloc.ptr<u8>[heap] 24
  Call.void @format(%m, %code)
  Call.void @print(%m)
  Release %m
  Br done
done:
  Ret.void
```

## When it applies

(1) `T` is the nearest common dominator of all uses of the allocation other than `Release`, `T` is not the allocating block `A`, and `A` dominates `T`; if no path from `A` avoids `T` nothing is gained and the pass does not fire. (2) `T` lies in no loop that does not also contain `A`. (3) The count and every other operand of the `Alloc` are available at `T`. (4) Every `Release` of the value is reached, from the region dominated by `T`, only through edges leaving that region, and the instructions between the region's exit and the `Release` do not use the value; the `Release` is then placed at the end of each exiting block. A `Release` inside the region stays where it is. (5) The handle is not `Shared` or `Moved` anywhere ([#348](https://github.com/Hawkynt/Polypiler/issues/348)) and `EscapeOf` is not `GlobalEscape` ([#82](https://github.com/Hawkynt/Polypiler/issues/82)) — a handle someone else may release cannot be given a new lifetime. (6) The allocation cannot fail observably — its count is a literal within the profile's allocation limit, or the profile declares allocation failure unobservable (the rule of [[opt-allocelim]]). Without that, paths that avoid `T` would lose an `OutOfMemory` trap they had, and on the path through `T` the trap would move behind the effects between `A` and `T` ([#115](https://github.com/Hawkynt/Polypiler/issues/115)). (7) No `PtrToInt` or ordered comparison exposes the address ([#121](https://github.com/Hawkynt/Polypiler/issues/121)): allocation order is then unobservable.

## How it is proven

`structural+alias-facts`: on paths through `T` the same region exists from before its first use to after its last; on other paths a region nobody used is neither created nor released. No solver, no timeout path.

## When it must not fire

- The using block is a loop body and the allocation stood before the loop: sunk, the function allocates once per iteration instead of once.
- A 1 GiB `Alloc[heap] %n` with unknown `n` at entry, used only on the error path: the source fails with `OutOfMemory` before doing any work; sunk, it writes half a file first and then fails — or does not fail at all on the common path.
- `%m` stored into a global on the using path and released by another function: moving the `Release` here frees it twice.
- A use in `done` as well as in `report`: the nearest common dominator is `entry` itself.
- `%k = PtrToInt %m` logged for diagnostics: the address changes with allocation order.

## Targets and cost gate

Every target that allocates explicitly, generated source included: the allocation appears inside the `if` that needs it, which is where a programmer would write it, and no cost component rises on any path (rule 2), so the cost model is not consulted. Target compilers cannot sink a call to an allocator whose failure behaviour they do not know.

## Switching it off

`--disable-opt O0341`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` after [#159](https://github.com/Hawkynt/Polypiler/issues/159) and [#500](https://github.com/Hawkynt/Polypiler/issues/500); enables `OPT-MEM-STACKPROMOTE` on the sunk allocation and [#156](https://github.com/Hawkynt/Polypiler/issues/156) of size computations on the other paths.
