# O0376 — Replace the overflow checks of a straight-line chain of operations by one test of their combined flags

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0376`.

## What it does

`r = a + b + c` under checked arithmetic is two `[trap Overflow]` additions. On a target without native
overflow traps [#497](https://github.com/Hawkynt/Polypiler/issues/497) turns each into a test and a branch placed exactly where the operation was, so a
chain of `n` operations costs `n` branches. When nothing observable happens between them, the program cannot
tell which operation overflowed first: computing the wrapped results, or-ing the overflow flags and testing once
gives the same trap after the same effects. PRD §4.8 (range propagation and its follow-ups), §17.1 (trap types
and ordering).

## Example

### Before

```
%t = Add.i32[trap Overflow] %a, %b
%r = Add.i32[trap Overflow] %t, %c
Store.i32 %out, %r
```

### After

```
  %p  = AddOverflow.i32 %a, %b
  %t  = Value %p
  %o1 = Overflow %p
  %q  = AddOverflow.i32 %t, %c
  %r  = Value %q
  %o2 = Overflow %q
  %o  = Or.bool %o1, %o2
  Br %o, ovf, cont
ovf:
  Trap.Overflow
cont:
  Store.i32 %out, %r
```

## When it applies

(1) All operations of the chain are in one block and one exception region ([#115](https://github.com/Hawkynt/Polypiler/issues/115)), carry the
same trap label and are `Add`, `Sub` or `Mul` on an integer type; the chain has 2 to 4 of them. (2) Every
instruction between the first and the last operation is pure and cannot trap ([#114](https://github.com/Hawkynt/Polypiler/issues/114)): no load, store,
call, division, checked conversion or `[bounds]` access. (3) Consequently a wrapped intermediate is read only
by pure instructions before the merged test; after the test every reader sees the values of an execution in
which no operation overflowed. (4) No value defined in the chain is used in a handler of the enclosing region
— a handler reached after the first operation trapped could not have read the later ones. (5) The merged test
stands where the last operation of the chain stood, so no effect moves across it. (6) [#155](https://github.com/Hawkynt/Polypiler/issues/155) scores the
flag form strictly below the trapping operations for the target profile; the original is listed first. The
source location of the merged trap is that of the first operation of the chain.

## How it is proven

`structural+exhaustive-8bit`: for every operand tuple the source traps iff at least one operation of the chain overflows on the wrapped inputs it receives, which is the disjunction of the flags; without a trap all values are equal. Checked over all 8-bit triples for every combination of two and three operations; chains of four follow by induction on the same step. No solver, no timeout path.

## When it must not fire

- Testing only the final result: `a = 100`, `b = 28`, `c = −1` at `i8` — the first addition overflows (128), the wrapped sum plus −1 is 127 and fits. The source traps; a final-range test does not.
- A store between the operations: when the second addition overflows, the source has performed the store; a merged test in front of it would lose the store, behind it nothing is merged.
- A call between the operations: the callee's effects come after the first check and before the second.
- Different labels (`Overflow` and `OverflowException`): the handler distinguishes them.
- A division between the operations: its own trap would be reordered relative to the first overflow.

## Targets

only targets whose profile lowers `[trap]` arithmetic to an explicit test — status-code targets of
[#497](https://github.com/Hawkynt/Polypiler/issues/497), C through overflow builtins, CPUs where the flag must be branched on. Targets with native
checked arithmetic (C# `checked`, Rust debug arithmetic) price the trapping operation at or below the flag
form; there the tie keeps the original and generated source keeps its checked operators.

## Switching it off

`--disable-opt O0376`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`; fires only on a strict cost win.

## Interactions with other passes

runs in phase `optimize` after [#163](https://github.com/Hawkynt/Polypiler/issues/163) has removed the checks ranges disprove and after [#158](https://github.com/Hawkynt/Polypiler/issues/158); splits the block at the merged test, so CFG analyses of the function are invalidated; [#497](https://github.com/Hawkynt/Polypiler/issues/497) then lowers one `Trap` instead of several trapping operations.
