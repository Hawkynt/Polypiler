# O0394 — Merge functions that differ in one constant or call target into one body with an extra parameter

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0394`.

## What it does

`OPT-IPA-ICF` folds functions that are identical. Generated and copy-pasted code is more often *nearly*
identical: two checksum routines with different moduli, accessors for two table bases, handlers that call
different leaf functions. Such a family is one function with one more parameter. Under `--opt size` that is a
plain win when the bodies are larger than the cost of passing the parameter at every call — the opposite trade
to cloning, bought back for size. PRD §4.8 (`--opt size`).

## Example

### Before

```
func @hashA(%x: u32) -> u32 {
  %a = Mul.u32[wrap] %x, 31
  %b = Add.u32[wrap] %a, 7
  %c = LShr.u32[strict] %x, 3
  %d = Xor.u32 %b, %c
  %e = URem.u32 %d, 65521
  %f = Add.u32[wrap] %e, 1
  %g = Shl.u32[strict] %f, 1
  %h = Or.u32 %g, 1
  Ret.u32 %h
}
func @hashB(%x: u32) -> u32 { ...the same nine instructions with 65519 in place of 65521... }
// two calls of each
```

### After

```
func @hashA(%x: u32, %ctx: u32) -> u32 {
  ...
  %e = URem.u32 %d, %ctx
  ...
}
// calls: @hashA(%p, 65521) twice, @hashA(%q, 65519) twice; @hashB is gone
```

## When it applies

(1) Every member of the group is owned (`ModuleOwnership` of `OPT-IPA-CONSTARG`) and not variadic.
(2) `FunctionCongruence.Compare` reports the same shape for every pair and all differences of the group lie at
one operand position (block, instruction, operand). (3) The differing operands are all integer or float
literals of the operand's type, all addresses of globals of one type, or all the callee operand of a direct
`Call` naming functions with identical signatures; a differing callee becomes a `FuncRef` argument and the
call a `CallIndirect`, which additionally requires that the target profile supports code references natively
or by index ([#480](https://github.com/Hawkynt/Polypiler/issues/480)). (4) A varying divisor literal keeps its trap contract: `URem.u32 %d, %ctx`
carries the contract of the original instruction. (5) Size rule: the representative — the first member in
module order — has at least 8 instructions, and `body × (members − 1) − calls − 1 > 0`, where `calls` is the
number of direct calls of all members and `body` the representative's instruction count. (6) A direct
self-call of a member corresponds to a self-call in the others and passes the context parameter on. (7)
[#155](https://github.com/Hawkynt/Polypiler/issues/155) scores the merged module strictly below the original under the size objective.

## How it is proven

`structural`: the merged body with the context parameter set to a member's value is that member's body, instruction for instruction; every call passes its member's value; recursion forwards it. No solver, no timeout path.

## When it must not fire

- A recursive variant whose merged body reloads the representative's constant in the recursive call: a call that entered for `hashB` computes with 65521 from the second level on.
- A varying divisor that is the literal 0 in one variant under a `[trap DivideByZero]` contract: the merged instruction must keep the contract, or that variant stops trapping.
- Two differences folded into one parameter because they happen to be equal in the sample pair: a third member differs in only one of them.
- An address-taken variant: indirect callers still use the old signature.
- Two 2-instruction functions with ten calls: one body saved, ten arguments added.

## Targets

every target under `--opt size`; decisive on memory-bound machine targets. Merging by call target
needs code references, so on targets with neither code pointers nor the index lowering only literal and
global-address differences merge. At other levels the rewrite is off and generated source keeps the variants
as separate functions.

## Switching it off

`--disable-opt O0394`; active at `--opt size` only; off at `balanced`, `speed`, `memory` and `none`.

## Interactions with other passes

module pass in phase `optimize` after `OPT-IPA-CONSTARG`, [#164](https://github.com/Hawkynt/Polypiler/issues/164), [#158](https://github.com/Hawkynt/Polypiler/issues/158) and `OPT-IPA-ICF` have maximized congruence; `OPT-IPA-DEADFUNC` has nothing left to do for the merged members; the merged function is excluded from `OPT-IPA-CONSTARG`'s view only in so far as its call sites now disagree by construction.
