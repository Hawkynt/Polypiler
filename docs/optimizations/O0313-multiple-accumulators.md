# O0313 — Split associative reductions into independent accumulators combined after the loop

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0313`.

## What it does

`sum = sum + a[i]` is a chain: the addition of iteration `k + 1` cannot start before that of iteration `k`
has finished. On a CPU that executes independent instructions in parallel the loop runs at the latency of one
addition per element however wide the machine is. Four accumulators, combined once after the loop, give four
independent chains. On an 8086 or a 6502 — no pipeline, no spare registers — the same rewrite is strictly a
loss, which is why PB-Compiler plans it target-gated. The rewrite reassociates the reduction and is therefore
legal only for operations proven associative and commutative. PRD §4.8, §23.5.

## Example

### Before

```
head:                                    // i = 0 .. n-1, n a multiple of 4 in this listing
  %i = Phi.u32 [pre: 0], [body: %i2]
  %s = Phi.u32 [pre: %init], [body: %s2]
  ...
body:
  %x  = Load.u32[inbounds #1] (ElemAddr.ptr<u32>[inbounds #1] %a, %i)
  %s2 = Add.u32[wrap] %s, %x
  %i2 = Add.u32[wrap] %i, 1
  Br head
```

### After

```
head:                                    // i = 0, 4, 8, ...
  %s0 = Phi.u32 [pre: %init], [body: %t0]
  %s1 = Phi.u32 [pre: 0],     [body: %t1]
  %s2 = Phi.u32 [pre: 0],     [body: %t2]
  %s3 = Phi.u32 [pre: 0],     [body: %t3]
  ...
body:
  %t0 = Add.u32[wrap] %s0, %x0           // %x0 … %x3: elements i, i+1, i+2, i+3
  %t1 = Add.u32[wrap] %s1, %x1
  %t2 = Add.u32[wrap] %s2, %x2
  %t3 = Add.u32[wrap] %s3, %x3
  ...
exit:
  %u = Add.u32[wrap] %s0, %s1
  %v = Add.u32[wrap] %s2, %s3
  %s = Add.u32[wrap] %u, %v
```

## When it applies

(1) `⊕` is proven associative and commutative on its type and its identity element is known:
0 for `Add`, `Or`, `Xor`, `UMax`; 1 for `Mul`; all-ones for `And`, `UMin`; the type's minimum for `SMax` and
maximum for `SMin`. (2) The accumulator phi is used inside the loop only by its own update, and the loop has
a single exit — an early exit or any other reader would observe a partial value. (3) The loop satisfies
[#513](https://github.com/Hawkynt/Polypiler/issues/513)'s preconditions for partial unrolling by `k` (trip count as an SSA expression before the
loop, [#350](https://github.com/Hawkynt/Polypiler/issues/350)); the copies stay in iteration order, so the body's loads, effects and traps are
untouched — only the reduction is reassociated. (4) The update carries `[wrap]` ([#122](https://github.com/Hawkynt/Polypiler/issues/122)) or is a
min/max. (5) The target gate of the blocking question holds and [#155](https://github.com/Hawkynt/Polypiler/issues/155) prefers the split form.

## How it is proven

`exhaustive-8bit+smt-bitvector` for associativity, commutativity and the identity of each operation (proven once per operation and width, 5 s budget, timeout → `not applied: proof timeout`), plus `structural` for the unrolling.

## When it must not fire

- `f64` sum of `[1e16, 1.0, -1e16, 1.0]`: in order it is 1.0; with two accumulators `(1e16 + -1e16) + (1.0 + 1.0)` is 2.0.
- `Add.i8[sat]` over `[100, 100, -100, -100]`: in order 100, 127, 27, −73; with two accumulators `(100 − 100) + (100 − 100)` is 0.
- `Add.i32[trap Overflow]` over `[2147483647, 1, -1, 0]`: the source traps at the second element; two accumulators hold 2147483646 and 1, and their sum 2147483647 never traps.
- `Sub.u32[wrap]` as the operation over `[1, 2, 3]` from 10: in order `((10 − 1) − 2) − 3 = 4`; with two accumulators `(10 − 1 − 3) − (0 − 2)` is 8.
- The body prints the running sum: every partial value is observable.

## Targets

Only machine-code backends for CPUs that overlap independent operations — out-of-order and
superscalar x86-64, AArch64 and comparable cores — and only at `--opt speed`. Never on 8/16-bit and
non-pipelined targets (6502, Z80, 8080, 8086, 65C816), where the extra accumulators cost registers and gain
nothing, and never on source-emitting targets: their compilers do this themselves when it pays (and cannot
for floats either), and four hand-carried partial sums are exactly the kind of output nobody wants to read.
How the gate is expressed in the cost model is the open question below.

## Switching it off

`--disable-opt O0313`; active at `--opt speed` on targets that pass the gate; off at `none`, `balanced`, `size` and `memory`.

## Interactions with other passes

runs in phase `specialize` after [#510](https://github.com/Hawkynt/Polypiler/issues/510) (a vectorized reduction already has one accumulator per lane and is not split again) and instead of [#513](https://github.com/Hawkynt/Polypiler/issues/513)'s plain partial unrolling for the loops it takes.
