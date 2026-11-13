# O0176 — Recover a counted loop from Duff's device

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#982](https://github.com/Hawkynt/Polypiler/issues/982). Once implemented it is switched off with `--disable-opt O0176`.

## What it does

Duff's device unrolls a loop eight times and handles the remainder by jumping into the middle of the
unrolled body through a `switch`. PB-Compiler emits the same shape as its Duff unrolling. The
control-flow graph is a loop with several entries — irreducible — so no loop analysis applies until the
device is recognized and replaced by the counted loop it implements. The classic form has a classic
defect: for a count of zero it runs the body eight times. The recovered count must say so unless zero is
excluded. PRD §19.2 (loop level: "Duff's device"), §19.5 S4, design requirement "recover counted
copy/processing semantics where provable". The recovered loop is a counted body over an
`IterationDomain` of [#201](https://github.com/Hawkynt/Polypiler/issues/201) (`range(lo, hi, step)`); `PolyIteration` in the listings is PRD
§22's name for that counted body and is spelled as [#201](https://github.com/Hawkynt/Polypiler/issues/201) defines it. The original
instructions are kept as the fallback graph with a `RecoveredFrom` link, and the report says "proven
equivalent to", never "intended as" (PRD §19.1).

## Example

### Before

```
entry:
  %passes = UDiv.u32 (Add.u32 %count, 7), 8
  %r = And.u32 %count, 7
  Switch %r, [0: c8, 7: c7, 6: c6, 5: c5, 4: c4, 3: c3, 2: c2, 1: c1]
c8:  Store.u8 %to++, (Load.u8 %from++)      ; falls through to c7 … c1
…
c1:  Store.u8 %to++, (Load.u8 %from++)
  %passes2 = Sub.u32 %passes, 1
  %more = ICmp.sgt.i32 %passes2, 0
  CondBr %more, c8, exit
```

### After

```
%zero = ICmp.eq.u32 %count, 0
%total = Select.u32 %zero, 8, %count        ; simply %count when count >= 1 is proven
%dom = IterationDomain range(0, %total, 1)
PolyIteration %dom, λ(%i):
  Store.u8 to[%i], (Load.u8 from[%i])
```

## When it applies

(1) the region has one entry block that branches on `count mod k` into a
fall-through chain of `k` body copies, case `r` entering the copy that leaves exactly `r` copies to
the end of the chain (`r = 0` entering the first copy); (2) the copies are isomorphic and advance the
element position by the same constant; (3) the back edge goes from the end of the chain to its first
copy under a pass counter that starts at `(count + k - 1) / k` (unsigned, without wrap of `count + k -
1`, proven or by range fact) and is decremented once per pass with the signed test `> 0` after the
decrement (for which a zero count yields exactly one pass); a test `!= 0` or an unsigned `> 0` is
accepted only together with a proof of `count ≥ 1`, because for a zero count it does not end after one
pass; (4) from these, the total number of body executions is `count` for `count ≥ 1` and `k` for
`count = 0`; the rule emits `count` only under a range fact or dominating guard `count ≥ 1`, otherwise
the `Select`; (5) the body copies execute in the same order in both forms, so no alias, bounds or
effect fact is needed and the body may have any effects; (6) position variables used after the region
are rebuilt from `total`.

## How it is proven

`smt-bitvector` on the pass-count arithmetic (total executions as a function of
`count`, including the wrap condition of `count + k - 1`) plus bounded symbolic execution of the
region's control flow for `count` ∈ 0..3k, comparing the sequence of executed body copies with the
loop's; isomorphism of the copies by the independent graph comparison. Reported as
`smt-bitvector+bounded-symbolic-execution`. A failed, unknown or timed-out obligation (budget 5 s
each) leaves the IR unchanged and reports `not applied: proof timeout` or the failing precondition.

## When it must not fire

- Zero count: with `count = 0` the classic device computes `passes = 0`, enters at the first copy,
  executes all eight copies, and the signed test `--passes > 0` then ends the loop: eight body
  executions. A loop over `count` executes none.
- Pass counter `count / k` (without rounding up): `count = 10`, `k = 8` gives one pass entered at the
  copy for remainder 2 — two executions instead of ten.
- Entry order mismatched (case 1 entering where case 7 should): the remainder is processed with the
  wrong number of copies.
- A copy that differs (the fifth stores a constant): every eighth element is treated differently.
- `count + 7` wrapping at the type's maximum: `passes` becomes 0 for a huge count.

## Switching it off

`--disable-opt O0176`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recover` before loop analysis (the region is irreducible until
rewritten) and before [#986](https://github.com/Hawkynt/Polypiler/issues/986), whose slice isomorphism it reuses; enables every loop
rule on the result.
