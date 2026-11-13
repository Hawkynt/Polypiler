# O0251 — Run cleanup regions on every non-exceptional exit edge in source order

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#759](https://github.com/Hawkynt/Polypiler/issues/759). Once implemented it is switched off with `--disable-opt O0251`.

## What it does

PRD §25.1: "Nonlocal `return`, `break`, `continue`, `goto`, exception, cancellation and coroutine suspension
need exact cleanup semantics." The exception lowering of the legalizer already runs `finally` on handled and
propagating paths; what remains are the jumps that leave a region without failing. C and PowerBASIC have no
`finally`, so the cleanup must be placed on each such edge, innermost first, after the return value was
computed.

## Example

### Before

```
%s = Scope.Enter.scope void, @fin
body: try(cleanup)
       %x = Load.i32 @x                 ; return x;
       Scope.Exit %s
       Ret.i32 %x
; @fin stores 2 to @x and logs "finally"
```

### After

```
body:  %x = Load.i32 @x                 ; value fixed first
       Call.void @fin()                 ; cleanup on the return edge
       Ret.i32 %x
; the exceptional entry into @fin stays with the exception lowering
```

## When it applies

(1) Each non-exceptional edge out of a region executes the region's cleanup
exactly once; an edge leaving n nested regions executes n cleanups, innermost first. (2) The operand of
`Ret` and the condition of a branch are evaluated before the cleanups of that edge. (3) Cleanup code
duplicated by `inline` is identical on every edge; under `dispatch` the edge number is a constant per edge
and the `Switch` lists every edge. (4) An edge that stays inside the region runs no cleanup. (5) Under
`finallyExit=forbidden` a cleanup containing `Ret` or a branch out of itself is a verifier error; under
`overrides` the cleanup's own exit replaces the edge's destination.

## How it is proven

`structural+interpreter`: validated by interpreting the region form and the lowered form
on every fixture and comparing the effect traces edge by edge.

## When it must not fire

- Evaluating the return expression after the cleanup: `int x = 1; try { return x; } finally { x = 2; }`
  returns 1; the naive order returns 2.
- Running the cleanup only at the lexical end of the region: `continue` inside the region skips it.
- Running the outer cleanup before the inner one on a `return` out of two nested regions.
- Running the cleanup twice — once on the `break` edge and once at the region end the edge then falls
  into.

## Switching it off

`--disable-opt O0251`; `Kind = Required`: the rule runs at every `--opt` level, including
`none`, on targets whose capabilities lack `Feature(finally)`. Disabled, the legalizer has no alternative
for these operations and reports `PLY0702`.

## Interactions with other passes

runs after [#767](https://github.com/Hawkynt/Polypiler/issues/767) and before the exception lowering; `inline` duplicates are
merged again by tail merging at `--opt size`.
