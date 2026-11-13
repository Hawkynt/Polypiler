# O0171 — Recover unit-step loops from partially unrolled loops

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#986](https://github.com/Hawkynt/Polypiler/issues/986). Once implemented it is switched off with `--disable-opt O0171`.

## What it does

A loop unrolled by four advances its index by 4 and repeats its body for `i`, `i+1`, `i+2`, `i+3`.
Compilers do it for speed; PB-Compiler has unrolling, unroll-and-jam and multiple-accumulator passes;
hand-written cipher and graphics loops do the same with constant trip counts. The unrolled loop hides
the element-wise structure from every sequence rule and forces the source's unroll factor on the target.
Recovering the unit-step loop needs the body repetition to be an affine family *and* the trip count to
be reconstructed exactly — a loop `for (i = 0; i < n; i += 4)` with four body copies touches
`ceil(n/4)·4` elements, not `n`. PRD §19.3, §19.5 S2, §24 S3 ("partial loop unrolling"). The recovered
loop is a counted body over an `IterationDomain` of [#201](https://github.com/Hawkynt/Polypiler/issues/201) (`range(lo, hi, step)`);
`PolyIteration` in the listings is PRD §22's name for that counted body and is spelled as
[#201](https://github.com/Hawkynt/Polypiler/issues/201) defines it. The original instructions are kept as the fallback graph with a
`RecoveredFrom` link, and the report says "proven equivalent to", never "intended as" (PRD §19.1).

## Example

### Before

```
loop:
  %i = Phi.u32 [0, entry], [%i4, body]
  %i3 = Add.u32 %i, 3
  %c = ICmp.ult.u32 %i3, %n          ; i + 4 <= n
  CondBr %c, body, exit
body:
  ; slice j = 0..3:  %x = Load.u8 src[%i + j];  %y = Xor.u8 %x, %key;  Store.u8 dst[%i + j], %y
  %i4 = Add.u32 %i, 4
  Br loop
```

### After

```
%m = And.u32 %n, 0xFFFFFFFC            ; floor(n / 4) * 4
%dom = IterationDomain range(0, %m, 1)
PolyIteration %dom, λ(%i):
  %x = Load.u8 src[%i]
  %y = Xor.u8 %x, %key
  Store.u8 dst[%i], %y
; %i after the loop is %m
```

## When it applies

(1) a natural loop with one exit and an induction variable with constant
step `k·s`, `k ≥ 2`; (2) the body is exactly `k` slices isomorphic under `i ↦ i + j·s`, plus the
induction update; conditions (2)–(6) of [#985](https://github.com/Hawkynt/Polypiler/issues/985) apply to the slices within one body
execution; (3) across body executions the unit-step loop executes the same slices in the same order —
slice `j` of execution `t` is iteration `t·k + j` — so no additional memory fact is needed for
sequential bodies; (4) the element count is derived from the exit test and proven: for a test
equivalent to `i + k·s ≤ n` the count is `floor(n/(k·s))·k`; for `i < n` it is `ceil(n/(k·s))·k`; the
arithmetic of the original test (wrap of `i + k - 1`, signedness) is part of the proof; (5) the
induction variable's exit value, if used after the loop, is rebuilt as the count times `s`; (6)
accumulator lanes are merged only with an associativity and commutativity proof for the combiner at
its type and no observation of individual lanes.

## How it is proven

`loop-inductive`: after `t` executions of the unrolled body the state equals the
unit-step loop's state after `t·k` iterations, discharged by SMT for one symbolic body execution
(which contains the `k` instantiated slices) under the facts; the count formula is an `smt-bitvector`
obligation on the exit test; bounded `exhaustive` interpretation for `n` ∈ 0..2k+1. A failed, unknown
or timed-out obligation (budget 5 s each) leaves the IR unchanged and reports `not applied: proof
timeout` or the failing precondition.

## When it must not fire

- Count for `i < n`: `for (i = 0; i < n; i += 4)` with four copies and `n = 6` processes elements
  0..7. A unit-step loop to `n` processes six; the two extra elements (and a possible out-of-bounds
  access) belong to the original's behaviour.
- Floating-point accumulators: `s0 += a[i]; s1 += a[i+1]` with `a = [16777216, 1, −16777216, 1]` in
  `f32`: `s0 = 0`, `s1 = 2`, total 2; a single accumulator in element order gives `((16777216 + 1) −
  16777216) + 1 = 1`.
- Non-consecutive copies (`i`, `i+1`, `i+2`, `i+4`): not an affine family; element `i+3` is skipped
  and `i+4` is processed twice across executions.
- Wrap in the exit test: `i + 3 < n` computed at `u8` wraps for `i ≥ 253`; the count formula must use
  the test as written.
- Interleaved body over overlapping buffers: as in [#985](https://github.com/Hawkynt/Polypiler/issues/985).

## Switching it off

`--disable-opt O0171`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recover` after [#985](https://github.com/Hawkynt/Polypiler/issues/985)'s slice machinery is available and
before [#987](https://github.com/Hawkynt/Polypiler/issues/987); enables the sequence and memory rules on the unit-step loop;
[#155](https://github.com/Hawkynt/Polypiler/issues/155) may re-unroll with its own factor.
