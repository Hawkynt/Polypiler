# O0382 — Merge functions with identical bodies and rebind the duplicates' call sites to one copy

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0382`.

## What it does

Generated code repeats itself: generic instantiations for types of the same representation, property
accessors, near-identical handlers after constant propagation removed their differences. Two functions with the
same body need one copy. Upstream folds them at the byte level in its assembler; Polypiler emits source for
most targets and has no bytes to compare, so the comparison has to happen on the IR, where it serves every
target. PRD §4.8 (`--opt size`).

## Example

### Before

```
func @ClearA(%p: ptr<u8>) -> void { Store.u8 %p, 0  Ret.void }
func @ClearB(%p: ptr<u8>) -> void { Store.u8 %p, 0  Ret.void }
func @main(%a: ptr<u8>, %b: ptr<u8>) -> void export "main" {
  Call.void @ClearA(%a)
  Call.void @ClearB(%b)
  Ret.void
}
```

### After

```
func @ClearA(%p: ptr<u8>) -> void { Store.u8 %p, 0  Ret.void }
func @main(%a: ptr<u8>, %b: ptr<u8>) -> void export "main" {
  Call.void @ClearA(%a)
  Call.void @ClearA(%b)
  Ret.void
}
```

## When it applies

Two functions are identical iff (1) parameter types and attributes, return type, role and function
attributes (contraction mode, variadic form, resumability) are equal; (2) they have the same number of blocks
in the same order, the same exception regions and handlers, and block by block the same number of
instructions; (3) corresponding instructions have the same opcode, type, contracts and attributes, and
corresponding operands are equal: the same literal by bits, the same global, the same external or module
function — or both the function under comparison itself — or SSA values and blocks at corresponding
positions. Source locations are not compared. A duplicate is folded only if it is owned (`ModuleOwnership` of
`OPT-IPA-CONSTARG`: not exported, not a `test` function, not address-taken); the representative may be
exported or address-taken. Folding is offered to [#155](https://github.com/Hawkynt/Polypiler/issues/155) under the size objective, the unfolded module
listed first.

## How it is proven

`structural`: identical functions compute identical results and effects for identical arguments by induction over their instructions; an owned duplicate has no identity the program can observe. No solver, no timeout path.

## When it must not fire

- An address-taken duplicate: `FuncRef @ClearA` and `FuncRef @ClearB` are different values; after folding a comparison or a table keyed by them changes.
- Functions equal except for the contraction mode (`fp-contract=on` against `off`): the same instructions may be fused in one and not in the other.
- Functions equal except for a trap label (`Overflow` against `OverflowException`): a handler distinguishes them.
- Equal instruction lists in a different block order with fall-through-sensitive lowering: compared positionally, they are not identical.
- Two exported duplicates: both names must stay callable from outside.

## Targets

every target under `--opt size`: fewer functions to emit. It matters most for memory-bound machine
targets and for output of monomorphization ([#472](https://github.com/Hawkynt/Polypiler/issues/472)). At other levels the rewrite is off, so generated
source keeps one target function per source function — two source procedures that happen to be identical stay
two.

## Switching it off

`--disable-opt O0382`; active at `--opt size` only; off at `balanced`, `speed`, `memory` and `none`.

## Interactions with other passes

module pass in phase `optimize`, after `OPT-IPA-CONSTARG`, [#164](https://github.com/Hawkynt/Polypiler/issues/164) and [#158](https://github.com/Hawkynt/Polypiler/issues/158) have maximized congruence and before `OPT-IPA-MERGEPARAM`; followed by `OPT-IPA-DEADFUNC`, which has nothing left to do for the folded duplicates.
