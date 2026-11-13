# O0356 — Rebalance a serial chain of a wrapping integer operation into a tree of logarithmic depth

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0356`.

## What it does

`a + b + c + d + e + f + g + h` parses into a chain seven additions deep, each waiting for the one
before. As `((a+b)+(c+d)) + ((e+f)+(g+h))` the same seven additions are three levels deep, and a CPU
that executes independent operations side by side finishes in less than half the time. The legality is
that of `OPT-RED-REASSOC`; the benefit exists only on targets that can overlap operations, which is
why it is its own, cost-gated rule (upstream O0121, planned in PB-Compiler). PRD §4.8.

## Example

### Before

```
%t1 = Add.u32[wrap] %a, %b
%t2 = Add.u32[wrap] %t1, %c
%t3 = Add.u32[wrap] %t2, %d
%t4 = Add.u32[wrap] %t3, %e
%t5 = Add.u32[wrap] %t4, %f
%t6 = Add.u32[wrap] %t5, %g
%r  = Add.u32[wrap] %t6, %h          // depth 7
```

### After

```
%p1 = Add.u32[wrap] %a, %b
%p2 = Add.u32[wrap] %c, %d
%p3 = Add.u32[wrap] %e, %f
%p4 = Add.u32[wrap] %g, %h
%q1 = Add.u32[wrap] %p1, %p2
%q2 = Add.u32[wrap] %p3, %p4
%r  = Add.u32[wrap] %q1, %q2         // depth 3
```

## When it applies

(1) The tree satisfies the node rule of `OPT-RED-REASSOC`: one opcode, one integer type, `[wrap]` for `Add`, `Sub` and `Mul` ([#118](https://github.com/Hawkynt/Polypiler/issues/118)); in an `Add` tree a `Sub[wrap]` node contributes its right subtree with flipped signs. (2) It has `4 ≤ n ≤ 32` leaves, every interior node has exactly one use and lies in the root's block — a shared intermediate is treated as a leaf, never recomputed. (3) Its current depth is greater than `ceil(log2 n)`; an already balanced tree is left alone, which makes the pass idempotent. (4) Rebuild: leaves keep their order; adjacent leaves are paired, an odd one is carried to the next level. For signed leaves a pair `(+a, +b)` becomes `Add a, b` (sign +), `(+a, −b)` becomes `Sub a, b` (+), `(−a, +b)` becomes `Sub b, a` (+), `(−a, −b)` becomes `Add a, b` (−); the leftmost leaf of a flattened tree is positive, so the root is positive and no `Neg` is ever needed. (5) The new instructions replace the old ones in the root's block; all are pure, so no trap or effect is involved. (6) `ICostModel.IsAvailable("expression.balanced")` is true and [#155](https://github.com/Hawkynt/Polypiler/issues/155) prefers the balanced alternative, priced with latency `ceil(log2 n) × L` against `(n − 1) × L` for the chain and equal size.

## How it is proven

`exhaustive-8bit+smt-bitvector+structural` inherited from `OPT-RED-REASSOC` for the identities, plus `exhaustive-4bit` over all tree shapes and sign patterns of 4–8 leaves for the rebuild. No additional solver obligation.

## When it must not fire

- Checked `i8` with the leaves 0, −100, 100, 100: the chain `((0 + (−100)) + 100) + 100` is 100 and never traps; the balanced `(0 + (−100)) + (100 + 100)` traps `Overflow` at `100 + 100`.
- A shared intermediate: `%t3` also feeds another instruction; rebuilding the tree leaves the old prefix alive and adds four operations.
- A 6502 or an 8086 with one accumulator: the balanced tree needs three values live at once and spills; the chain needs none.
- Signed leaves handled carelessly: `((a + b) − c) + d` balanced as `(a + b) − (c + d)` is wrong; the correct form is `(a + b) − (c − d)`. With `a = 5, b = 3, c = 10, d = 4` on `u8` the chain gives 2, the wrong form gives 250.
- Floating point: `(((1e16 + 1) + 1) + 1)` is 1e16, `(1e16 + 1) + (1 + 1)` is 1.0000000000000002e16.

## Targets and cost gate

Pays off only on targets that execute independent operations concurrently and where Polypiler's instruction order reaches the hardware: machine-code backends for out-of-order CPUs. Such a profile lists the form `expression.balanced`. Profiles of source-language targets do not: their compilers rebalance integer chains themselves, and a sum rewritten as nested pairs is less readable than the chain the programmer wrote. Profiles of single-accumulator and stack machines (6502, Z80, 8086, the JVM and CLR evaluation stacks) do not either: there the chain is the cheapest form. Without the form the pass does nothing. The form name `expression.balanced` and the `forms` list of `cost.yaml` that carries it are defined by `OPT-MEM-FORMS`.

## Switching it off

`--disable-opt O0356`; active at `--opt balanced` and `speed`, off at `--opt size`, `memory` and `none`.

## Interactions with other passes

runs in phase `specialize` after `OPT-RED-REASSOC` and [#158](https://github.com/Hawkynt/Polypiler/issues/158) (so shared subtrees are already shared and count as leaves) and after [#510](https://github.com/Hawkynt/Polypiler/issues/510) (a chain that became a vector reduction is not balanced as scalars); `OPT-RED-FPREASSOC` reuses the rebuild.
