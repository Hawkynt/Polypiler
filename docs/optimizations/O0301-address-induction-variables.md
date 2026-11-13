# O0301 — Carry element addresses that are affine in a loop counter as stepped pointers

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0301`.

## What it does

`sum += a[i]` recomputes `base + i * elementSize` in every iteration. On a 6502, a Z80 or an 8086 that is a
multiplication or a chain of shifts and adds per element; a person writing assembly keeps a pointer and
advances it. PB-Compiler's `AddressInduction` does exactly that and brings its accumulate loop from ten
instructions per iteration to six (upstream's figure, on x86-16). Polypiler has the same loops but typed
pointers with bounds, so the rewrite needs one condition upstream does not: the stepped pointer must stay
inside its allocation. PRD §4.8 (strength reduction), §23.5 (costs from the target profile).

## Example

### Before

```
head:
  %i = Phi.u32 [pre: 0], [body: %i2]
  %s = Phi.u16 [pre: 0], [body: %s2]
  %c = ICmp.ult.u32 %i, 4
  Br %c, body, exit
body:
  %e  = ElemAddr.ptr<u16>[inbounds #1] %a, %i
  %x  = Load.u16[inbounds #1] %e
  %s2 = Add.u16[wrap] %s, %x
  %i2 = Add.u32[wrap] %i, 1
  Br head
```

### After

```
pre:
  %e0 = ElemAddr.ptr<u16>[inbounds #1] %a, 0
  Br head
head:
  %i = Phi.u32 [pre: 0], [body: %i2]
  %e = Phi.ptr<u16> [pre: %e0], [body: %e2]
  %s = Phi.u16 [pre: 0], [body: %s2]
  %c = ICmp.ult.u32 %i, 4
  Br %c, body, exit
body:
  %x  = Load.u16[inbounds #1] %e
  %s2 = Add.u16[wrap] %s, %x
  %e2 = PtrAdd.ptr<u16>[unchecked] %e, 1
  %i2 = Add.u32[wrap] %i, 1
  Br head
```

## When it applies

(1) The array operand is loop-invariant and no instruction of the loop may resize or replace
it ([#114](https://github.com/Hawkynt/Polypiler/issues/114), [#82](https://github.com/Hawkynt/Polypiler/issues/82); the length-stability rule of [#502](https://github.com/Hawkynt/Polypiler/issues/502)). (2) The index is
`AddRec(start, step)` with loop-invariant `start` and constant `step ≠ 0`, and the recurrence does not wrap
in the index type over the loop's execution (the no-wrap flag of [#350](https://github.com/Hawkynt/Polypiler/issues/350)) — an index that wraps makes
the pointer jump backwards by 2^w elements, which a stepped pointer does not do. (3) The address is
`[inbounds #p]` or `[unchecked]`; a `[bounds]` address traps on the index and is not rewritten. (4) Both the
entry address `start` and the exit address `start + trips·step` are proven to lie in `[0, Length]` elements
([#94](https://github.com/Hawkynt/Polypiler/issues/94), one past the end allowed, as [#123](https://github.com/Hawkynt/Polypiler/issues/123) permits): the preheader forms the entry address
even when the loop runs zero times, and the latch forms the exit address after the last iteration. Without
both proofs the rewrite does not fire. (5) The target has `Pointer.Native` and is not `emits: source`
([#259](https://github.com/Hawkynt/Polypiler/issues/259), [#262](https://github.com/Hawkynt/Polypiler/issues/262)), and [#155](https://github.com/Hawkynt/Polypiler/issues/155) ranks `PtrAdd` below the address computation it
replaces.

## How it is proven

`structural+range-facts+alias-facts`: by induction the phi holds `ElemAddr(a, start + k·step)` in iteration `k`; (4) keeps every intermediate pointer inside the allocation. No solver, no timeout path.

## When it must not fire

- `for (i = 0; i < 4; i += 3) a[i]` on an array of 4 elements: accesses 0 and 3 are in bounds, but the latch would form the address of element 6, outside the allocation.
- `for (i = 5; i < n; i++) a[i]` with `n = 0` and an empty array: the loop never runs, but the preheader would form the address of element 5.
- A `u8` index stepping by 100: 0, 100, 200, 44 — the fourth access is element 44, the stepped pointer is at element 300.
- The body appends to the list that owns the storage: the stepped pointer still points into the old buffer.
- `ElemAddr[bounds] %a, %i` with `i` reaching the length: the source traps `IndexOutOfRange` at the address; a stepped pointer would have to trap at the same instruction with the same label.

## Targets

Machine-code and assembly backends, most of all those without a multiply or a scaled addressing
mode (6502, Z80, 8080, 8086). Not source-emitting targets: C#, JavaScript, Python or BASIC have no pointer to
step, and C and Rust compilers perform this reduction themselves — there the subscript form is what a reader
expects, so the pass reports `source target keeps the index form` and changes nothing. [#155](https://github.com/Hawkynt/Polypiler/issues/155) is the
gate on the remaining targets: alternatives `index` (`ElemAddr` per iteration) and `pointer` (`PtrAdd` per
iteration plus one `ElemAddr` in the preheader), listed in that order so that a tie keeps the index form.

## Switching it off

`--disable-opt O0301`; active at `--opt balanced`, `speed`, `size` and `memory` on `Pointer.Native` targets that are not `emits: source`; off at `--opt none`.

## Interactions with other passes

runs in phase `specialize`, after [#510](https://github.com/Hawkynt/Polypiler/issues/510) and the recognition rules ([#273](https://github.com/Hawkynt/Polypiler/issues/273), [#277](https://github.com/Hawkynt/Polypiler/issues/277)), which match the counter-indexed form this pass removes, and after [#502](https://github.com/Hawkynt/Polypiler/issues/502), which supplies the `[inbounds]` contracts; enables `OPT-LOOP-COUNTDOWN` (the counter is no longer read by the body).
