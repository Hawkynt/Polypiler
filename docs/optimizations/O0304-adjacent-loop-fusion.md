# O0304 — Fuse adjacent loops with equal trip counts when no dependence is reversed

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0304`.

## What it does

`for i: a[i] = i` followed by `for i: b[i] = a[i] * 2` walks the same range twice and reads `a` back from
memory after it has left the cache — or, on a small machine, simply pays the loop overhead twice.
[#507](https://github.com/Hawkynt/Polypiler/issues/507) fuses map/filter/reduce pipelines and names this case as out of scope ("fusing two independent
loops over the same range → not planned yet"). This issue plans it for block loops, with the dependence
condition stated: fusion may not let the second loop's iteration `q` run before an iteration `p > q` of the
first loop that it depends on. PRD §4.8 (iteration fusion).

## Example

### Before

```
A.head:                                  // i = 0 .. 99
  %i = Phi.u32 [pre: 0], [A.body: %i2]
  ...
A.body:
  Store.u16[inbounds #1] (ElemAddr.ptr<u16>[inbounds #1] %a, %i), (Truncate.u16 %i)
  %i2 = Add.u32[wrap] %i, 1
  Br A.head
B.head:                                  // j = 0 .. 99
  %j = Phi.u32 [A.exit: 0], [B.body: %j2]
  ...
B.body:
  %x = Load.u16[inbounds #1] (ElemAddr.ptr<u16>[inbounds #1] %a, %j)
  %y = Mul.u16[wrap] %x, 2
  Store.u16[inbounds #2] (ElemAddr.ptr<u16>[inbounds #2] %b, %j), %y
  %j2 = Add.u32[wrap] %j, 1
  Br B.head
```

### After

```
head:                                    // i = 0 .. 99
  %i = Phi.u32 [pre: 0], [body: %i2]
  ...
body:
  Store.u16[inbounds #1] (ElemAddr.ptr<u16>[inbounds #1] %a, %i), (Truncate.u16 %i)
  %x = Load.u16[inbounds #1] (ElemAddr.ptr<u16>[inbounds #1] %a, %i)
  %y = Mul.u16[wrap] %x, 2
  Store.u16[inbounds #2] (ElemAddr.ptr<u16>[inbounds #2] %b, %i), %y
  %i2 = Add.u32[wrap] %i, 1
  Br head
```

## When it applies

(1) Adjacency and equal iteration spaces as in scope; both trip counts are `Exact` and equal or
the same `Symbolic` value and offset. (2) Scalars: `B` uses no SSA value defined in `A`'s loop (it would need
`A`'s final value in every iteration). (3) Memory: for every access `x` of `A` and `y` of `B`, at least one
a store, that may touch the same storage: both are to the same array with subscripts `i + c1` and `j + c2`
of the common counter ([#350](https://github.com/Hawkynt/Polypiler/issues/350), equal step), and `c2 ≤ c1` — then `B`'s iteration `q` meets `A`'s
iteration `p = q + c2 − c1 ≤ q`, which the fused loop still executes first. `c2 > c1` is a fusion-preventing
dependence. Different arrays must be `NoAlias` ([#82](https://github.com/Hawkynt/Polypiler/issues/82)); non-uniform subscripts, `MayAlias` and
`Unknown` block the rewrite. (4) Both loops are clean: no effect other than non-volatile element loads and
stores ([#114](https://github.com/Hawkynt/Polypiler/issues/114), [#80](https://github.com/Hawkynt/Polypiler/issues/80)) and no instruction that may trap ([#115](https://github.com/Hawkynt/Polypiler/issues/115), facts from
[#94](https://github.com/Hawkynt/Polypiler/issues/94)) — a trap in `A`'s iteration 5 must not find `B`'s iterations 0 … 4 already stored, and a trap
in `B` must not find `A` unfinished. (5) [#155](https://github.com/Hawkynt/Polypiler/issues/155) does not rank the fused loop above the pair.

## How it is proven

`structural+alias-facts+effect-facts+range-facts`: under (2)–(4) every pair of conflicting accesses keeps its order, and nothing else is observable. No solver, no timeout path.

## When it must not fire

- `for i in 0..2: a[i] = 10 * a[i]` then `for j in 0..2: b[j] = a[j + 1]` on `a = [1, 2, 3, 4]`: separately `b = [20, 30, 4]`; fused, `b[0]` reads `a[1]` before it is multiplied and `b = [2, 3, 4]`.
- The second loop sums into `t` and uses the first loop's sum `s` in every iteration (`t += s * b[j]`): `s` is not final until the first loop ends.
- The first loop contains `x / d` with `d` possibly 0 in iteration 5: separately the second loop has stored nothing when the trap occurs; fused, five elements of `b` are already written.
- `b` may alias `a` (two pointer parameters): the second loop's stores can overwrite elements the first loop has not processed yet.
- Trip counts `n` and `n + 1`: the last iteration of the second loop has no partner.

## Targets

Every target: one loop instead of two saves the loop overhead once per element, and neither a
mature C compiler (GCC and Clang do not fuse loops by default) nor a BASIC interpreter does it. Generated
source becomes shorter and stays readable. [#155](https://github.com/Hawkynt/Polypiler/issues/155) ranks `two loops` against `fused` with `LoopCost`
(the fused form saves one `loop.overhead` per iteration and one loop skeleton in size), original first, so
that a profile on which nothing is saved keeps the source's shape.

## Switching it off

`--disable-opt O0304`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` after [#502](https://github.com/Hawkynt/Polypiler/issues/502) (which makes accesses `[inbounds]` and the loops clean) and before [#510](https://github.com/Hawkynt/Polypiler/issues/510) and [#513](https://github.com/Hawkynt/Polypiler/issues/513); `OPT-LOOP-DISTRIBUTE` is its inverse and is prevented from oscillating with it by legality (4).
