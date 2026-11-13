# O0358 — Rebalance float add and multiply trees whose every partial result is proven exact

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0358`.

## What it does

Floating-point addition is not associative: regrouping changes where rounding happens. PB-Compiler
therefore rebalances float trees only under `$OPTIMIZE SPEED`, which grants the fast-math permission
`reassoc` (upstream O0344), and the candidate list words the rule as "only under an explicit source
permission to reassociate". PolyIR evaluates floating-point operations as written: `docs/adr/0002-numeric-contracts.md` (decision 6) admits no fast-math, reassociation or reciprocal flag, `docs/adr/0005-floating-point-tolerance.md` reserves the `fast` profile without a licence, and PRD §4.1 forbids unproven reassociation. No such permission can
be expressed today, so that tier cannot be borrowed. What can be is the case where regrouping changes
*nothing*: when every partial sum or product in every grouping is exactly representable, no rounding
happens at all. That is the common situation in code translated from languages whose only number type
is a double — counters, indices and pixel sums held in `f64` on a target without 64-bit integers.

## Example

### Before

```
// facts: %a..%d integral, each in [0, 2^51]
%t1 = FAdd.f64[rne] %a, %b
%t2 = FAdd.f64[rne] %t1, %c
%r  = FAdd.f64[rne] %t2, %d           // depth 3
```

### After

```
%p1 = FAdd.f64[rne] %a, %b
%p2 = FAdd.f64[rne] %c, %d
%r  = FAdd.f64[rne] %p1, %p2          // depth 2
```

## When it applies

(1) Every node is `FAdd`/`FSub` (one tree kind) or `FMul` (the other) in the same format and with the same rounding mode ([#116](https://github.com/Hawkynt/Polypiler/issues/116)); `4 ≤ n ≤ 32` leaves; every interior node has one use and lies in the root's block; the current depth exceeds `ceil(log2 n)`. (2) *Exactness*: for every leaf, `ValueFacts.FloatAt` gives `Integral = true`, `IsFinite` and both `Min` and `Max` ([#347](https://github.com/Hawkynt/Polypiler/issues/347)); let `M_i = max(|Min|, |Max|)`. For an add/subtract tree `Σ M_i ≤ 2^p`; for a multiply tree `Π max(M_i, 1) ≤ 2^p`; the sums and products are computed in exact integer arithmetic. Then every partial result of every grouping is an integer of magnitude at most `2^p`, hence representable, hence produced without rounding, and all groupings agree as real numbers. (3) *Signed zero*: all groupings also agree on the sign of a zero result — a sum is `−0` exactly when every leaf, taken with its sign in the tree, is `−0` (in the round-down mode: `+0` exactly when every signed leaf is `+0`), and a product's sign is the product of the leaf signs — so no fact about `−0` is needed. (4) No leaf can be NaN or infinite by (2), so NaN propagation order is not in question. (5) A fact that is `Unknown`, not integral, unbounded or infinite on any leaf blocks the tree; so does a bound over the limit. (6) Rebuild and cost gate are those of `OPT-RED-TREEBALANCE`: `BalancedTree.Build`, the profile form `expression.balanced`, and [#155](https://github.com/Hawkynt/Polypiler/issues/155) preferring the lower latency.

## How it is proven

`float-exact-range`: under (2) each operation of the old and of the new tree computes its mathematical result exactly, so both trees compute the same real number, and by (3) the same zero. No solver, no timeout path; the bound arithmetic uses arbitrary-precision integers.

## When it must not fire

- `(((2^53 + 1) + 1) + 1)` in `f64` is 9007199254740992 — each `+ 1` rounds back down — while `(2^53 + 1) + (1 + 1)` is 9007199254740994. The bound sum 2^53 + 3 exceeds 2^53 and the tree is refused.
- `((0.1 + 0.2) + 0.3) + 0.4`: the leaves are not integral; `(0.1 + 0.2) + 0.3` alone is already 0.6000000000000001 against 0.6 for the other grouping.
- `(((1e16 + 1) + 1) + 1)` is 1e16 and `(1e16 + 1) + (1 + 1)` is 1.0000000000000002e16: integral leaves, but 1e16 > 2^53.
- Finite, integral leaves far beyond the bound: with `a = b = 1e308` and `c = d = −1e308` the chain `((a + b) + c) + d` is +∞ and the balanced `(a + b) + (c + d)` is `∞ + (−∞)`, a NaN.
- A multiply tree with leaves bounded by 2^20 each: three of them already reach 2^60 > 2^53.
- Mixed rounding modes in one tree: the nodes are not one operation family.

## Targets and cost gate

The cost gate is that of `OPT-RED-TREEBALANCE`: only profiles that list `expression.balanced` — machine-code backends for CPUs that overlap independent operations — and never at `--opt size`. For source-language targets this matters more than for integers: their compilers are *forbidden* to regroup floats without fast-math, so the pass is the only way such a sum gets balanced, but their profiles still decide, and a profile for readable output does not list the form. On a JavaScript target, where integral values stay doubles for lack of a 64-bit integer, the engine's JIT executes the emitted order as written, so the profile lists it. The form name `expression.balanced` and the `forms` list of `cost.yaml` that carries it are defined by `OPT-MEM-FORMS`.

## Switching it off

`--disable-opt O0358`; active at `--opt balanced` and `speed`, off at `--opt size`, `memory` and `none`.

## Interactions with other passes

runs in phase `specialize` after [#264](https://github.com/Hawkynt/Polypiler/issues/264) (trees it turned into integers are handled by `OPT-RED-TREEBALANCE`), after [#506](https://github.com/Hawkynt/Polypiler/issues/506) (a fused multiply-add is not a tree node) and after [#510](https://github.com/Hawkynt/Polypiler/issues/510); shares `BalancedTree` with `OPT-RED-TREEBALANCE`.
