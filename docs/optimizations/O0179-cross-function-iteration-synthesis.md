# O0179 — Synthesize iterations across function boundaries under call-effect summaries

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#980](https://github.com/Hawkynt/Polypiler/issues/980). Once implemented it is switched off with `--disable-opt O0179`.

## What it does

Element-wise work is often hidden behind a call: a loop that calls `put(dst, i, f(get(src, i)))`, or
four consecutive calls `step(buf, 0); step(buf, 1); step(buf, 2); step(buf, 3)` that a programmer or
compiler unrolled. The intraprocedural rules stop at the call: its effects are unknown, so the loop is
neither a `Map` nor recoverable from its unrolled form. With an effect summary for the callee — which
memory it reads and writes as a function of its arguments, whether it can throw, whether it keeps state
— the same reasoning applies across the boundary. Without one, the calls stay calls. PRD §19.5 S5
("cross-function and mixed ASM/iteration synthesis subject to call-effects constraints"), §17.2
(`External.Call`), §17.5. The recovered loop is a counted body over an `IterationDomain` of
[#201](https://github.com/Hawkynt/Polypiler/issues/201) (`range(lo, hi, step)`); `PolyIteration` in the listings is PRD §22's name for that
counted body and is spelled as [#201](https://github.com/Hawkynt/Polypiler/issues/201) defines it. The original instructions are kept as the
fallback graph with a `RecoveredFrom` link, and the report says "proven equivalent to", never "intended
as" (PRD §19.1).

## Example

### Before

```
Call @put, %buf, 0, 0
Call @put, %buf, 1, 3
Call @put, %buf, 2, 6
Call @put, %buf, 3, 9
```

### After

```
%dom = IterationDomain range(0, 4, 1)
PolyIteration[order=sequential] %dom, λ(%i):
  %v = Mul.u32 %i, 3
  Call @put, %buf, %i, %v
```

## When it applies

*Call sequences*: (1) the calls target the same resolved function with the
same calling convention; (2) each argument position is either the same SSA value in all calls or a
constant/affine expression in the call number; (3) nothing with effects lies between the calls;
argument computations are pure; (4) results of the calls, if used, are used in a way expressible per
iteration (stored at an affine position, folded into an accumulator) — otherwise refused. Because the
calls happen in the same order and number, no summary is needed for this form. *Summarized bodies*:
(5) every call in the body has an interprocedural summary from [#80](https://github.com/Hawkynt/Polypiler/issues/80) that is complete (no
`Unknown` component); (6) the summary's write set is confined to the element(s) addressed by the
iteration index, its read set does not include elements written by other iterations, it reads and
writes no global or static state, performs no device or external effect, and terminates; (7) if the
callee can throw or trap, the resulting operation is `order=sequential` and the exception contract of
[#115](https://github.com/Hawkynt/Polypiler/issues/115) is attached; (8) the calling convention and argument types match the callee's
declaration; (9) the summary is recorded as an obligation: if the callee changes, the proof is void
and the rule must be re-run.

## How it is proven

structural for call sequences (same calls, same order; the instantiated loop is
compared with the original sequence by `smt-bitvector` on the argument values); `range+alias facts`
for summarized bodies (`EffectFact` obligations instantiating the callee summary per iteration) plus
the `loop-inductive` obligation of the sequence rule that consumes the result; bounded `exhaustive`
interpretation with the callee's real body for `k`, `n` ≤ 4. A failed, unknown or timed-out obligation
(budget 5 s each) leaves the IR unchanged and reports `not applied: proof timeout` or the failing
precondition.

## When it must not fire

- Stateful callee: `next()` increments a static counter and returns it. Four calls return 1, 2, 3, 4
  in order; as an unordered `Map` the values could be assigned in any order, and a fused pipeline
  could call it fewer times.
- A callee that writes the next element: `fill(dst, i)` writes `dst[i + 1]` as well; iteration `i + 1`
  then reads or overwrites it — a loop-carried dependence invisible at the call site.
- A store between two calls of a sequence: the third call may read the global that was just written; a
  loop of four calls moves the store before or after all of them.
- An unknown callee: any effect is possible, including writing the loop's own bound.
- A calling-convention mismatch (a `PASCAL` routine called as `CDECL` through a declaration): the call
  must not be re-emitted through a different convention on the strength of matching names.
- A callee that may not terminate or may throw at element 2: `order=any` would let later elements be
  processed first.

## Switching it off

`--disable-opt O0179`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recover` after interprocedural effect analysis and after
[#985](https://github.com/Hawkynt/Polypiler/issues/985) (which handles the non-call statements between or around the calls) and
[#978](https://github.com/Hawkynt/Polypiler/issues/978) (which supplies semantics for assembly callees); requests a `recognize` sweep
through `PassContext.RequestSweep` for the sequence rules; [#155](https://github.com/Hawkynt/Polypiler/issues/155) may reselect the fallback.
