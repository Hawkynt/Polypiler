# O0175 — Recover the logical loop from two-stage software-pipelined loops

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#984](https://github.com/Hawkynt/Polypiler/issues/984). Once implemented it is switched off with `--disable-opt O0175`.

## What it does

Software pipelining overlaps consecutive iterations: the loop body finishes iteration `i-1` and starts
iteration `i`, a prologue starts the first iteration and an epilogue finishes the last. PB-Compiler has
such a pass; optimizing compilers for in-order CPUs produce it routinely. The logical loop — load,
compute, store per element — is spread over three places, and no sequence rule can see it. Collapsing
the pipeline moves each iteration's late stage back before the next iteration's early stage; PRD §19.3
allows that only when the overlap of iterations, possible faults and dependency order are proven
equivalent. PRD §19.2 (dataflow level), §19.5 S4, §24 S4. The recovered loop is a counted body over an
`IterationDomain` of [#201](https://github.com/Hawkynt/Polypiler/issues/201) (`range(lo, hi, step)`); `PolyIteration` in the listings is PRD
§22's name for that counted body and is spelled as [#201](https://github.com/Hawkynt/Polypiler/issues/201) defines it. The original
instructions are kept as the fallback graph with a `RecoveredFrom` link, and the report says "proven
equivalent to", never "intended as" (PRD §19.1).

## Example

### Before

```
entry:                                   ; requires n >= 1
  %t0 = Load.u8 a[0]                     ; prologue: stage 1 of element 0
  Br loop
loop:
  %i = Phi.u32 [1, entry], [%i2, body]
  %t = Phi.u8 [%t0, entry], [%t2, body]
  %c = ICmp.ult.u32 %i, %n
  CondBr %c, body, exit
body:
  %y = Add.u8 %t, 10                     ; stage 2 of element i-1
  Store.u8 b[%i - 1], %y
  %t2 = Load.u8 a[%i]                    ; stage 1 of element i
  %i2 = Add.u32 %i, 1
  Br loop
exit:
  %ye = Add.u8 %t, 10                    ; epilogue: stage 2 of element n-1
  Store.u8 b[%n - 1], %ye
```

### After

```
%dom = IterationDomain range(0, %n, 1)
PolyIteration %dom, λ(%i):
  %t = Load.u8 a[%i]
  %y = Add.u8 %t, 10
  Store.u8 b[%i], %y
```

## When it applies

(1) the loop has one exit, a unit-step index, and loop-carried values that
are exactly the stage-1 results of the previous element; (2) the body splits into a stage-2 part that
uses only carried values, invariants and the index minus one, and a stage-1 part that uses only
invariants and the index; (3) the prologue is the stage-1 part instantiated at the first index and the
epilogue the stage-2 part at the last, by graph isomorphism; (4) the trip count covers the same
elements: the pipelined code executes stage 1 and stage 2 once for each of `n` elements only if `n ≥
1` — a dominating guard or a range fact must prove it, since the unguarded form performs the prologue
load and the epilogue store even for `n = 0`; (5) *order*: in the store-then-load body order the
collapsed loop performs the same accesses in the same order and needs no memory fact; in the
load-ahead order the collapse moves the load of element `i` after the store of element `i-1`, which
requires [#82](https://github.com/Hawkynt/Polypiler/issues/82) to prove that store and load cannot alias, and requires the load to be proven
non-trapping (in the original it happens before the store, in the collapsed loop after it); (6)
volatile and device accesses are accepted only in the order-preserving form; (7) no other effects in
prologue, body or epilogue; carried values are not used after the loop except by the epilogue.

## How it is proven

`loop-inductive`: after the prologue and `t` body executions the state equals that of
the collapsed loop after `t` complete iterations plus stage 1 of iteration `t`, discharged by SMT for
one symbolic body execution; alias, bounds and range facts as obligations; bounded `exhaustive`
interpretation for `n` ∈ 1..4 over all placements of `a` and `b` in a 6-byte buffer. If the solver
exceeds its budget, the rule does not fire. A failed, unknown or timed-out obligation (budget 5 s
each) leaves the IR unchanged and reports `not applied: proof timeout` or the failing precondition.

## When it must not fire

- Load-ahead with overlap: `a = 01 02 03`, `b = a + 1`, `n = 2`, body `b[i] = a[i] + 10`. Pipelined
  with load-ahead: load `a[0] = 1`; load `a[1] = 2`, store `b[0]` (= `a[1]`) = 11; epilogue stores
  `b[1]` (= `a[2]`) = 12 → `01 0B 0C`. Collapsed: `a[1] = 11`, then `b[1] = a[1] + 10 = 21` → `01 0B
  15`.
- Fault order: in the load-ahead form a faulting load of `a[i]` occurs before the store of `b[i-1]`;
  collapsed, the store has already happened.
- `n = 0` without a guard: the pipelined code still loads `a[0]` and stores `b[n-1]`; the collapsed
  loop does nothing.
- An epilogue that differs from stage 2 (a final rounding, a different constant): the last element is
  computed differently.
- A carried value used after the loop for something else: stage 1 of the last element is observable
  separately.

## Switching it off

`--disable-opt O0175`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recover` after loop canonicalization and alias analysis and after
[#987](https://github.com/Hawkynt/Polypiler/issues/987) (which shares the fragment-isomorphism machinery); enables the sequence and
memory rules on the collapsed loop; [#155](https://github.com/Hawkynt/Polypiler/issues/155) may re-pipeline for an in-order target.
