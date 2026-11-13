# O0323 — Clone a loop without its bounds checks behind one preheader guard and keep the checked loop as fallback

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0323`.

## What it does

[#502](https://github.com/Hawkynt/Polypiler/issues/502) removes a bounds check only when the facts already prove it; for `for (i = 0; i < n; i++)
a[i] = …` with an unrelated `n` they do not, and the check stays in every iteration — where it also keeps
[#510](https://github.com/Hawkynt/Polypiler/issues/510), [#508](https://github.com/Hawkynt/Polypiler/issues/508) and [#958](https://github.com/Hawkynt/Polypiler/issues/958) away, because they need trap-free bodies. One test before the
loop can establish the fact for the whole range; if it fails, the original loop runs and traps exactly where
it always did. [#502](https://github.com/Hawkynt/Polypiler/issues/502) and [#514](https://github.com/Hawkynt/Polypiler/issues/514) both point to [#778](https://github.com/Hawkynt/Polypiler/issues/778) for this, and [#778](https://github.com/Hawkynt/Polypiler/issues/778)
declares guards on loop bounds out of its own scope; this issue fills that gap with PB-Compiler's
`LoopVersioning`. PRD §6.3 (bounds proofs), §4.8.

## Example

### Before

```
pre:
  Br head
head:
  %i = Phi.u32 [pre: 0], [body: %i2]
  %c = ICmp.ult.u32 %i, %n
  Br %c, body, exit
body:
  Store.u16[bounds] (ElemAddr.ptr<u16>[bounds] %a, %i), (Truncate.u16 %i)     // %a: array<u16, 32>
  %i2 = Add.u32[wrap] %i, 1
  Br head
```

### After

```
// proofs { #1 bounds: "0 <= %i.f", "%i.f < %n", "%n <= 32" }
pre:
  %g = ICmp.ule.u32 %n, 32
  Br %g, fast.head, head
fast.head:
  %i.f = Phi.u32 [pre: 0], [fast.body: %i2.f]
  %c.f = ICmp.ult.u32 %i.f, %n
  Br %c.f, fast.body, exit
fast.body:
  Store.u16[inbounds #1] (ElemAddr.ptr<u16>[inbounds #1] %a, %i.f), (Truncate.u16 %i.f)
  %i2.f = Add.u32[wrap] %i.f, 1
  Br fast.head
head: ...                                // the original loop, unchanged
```

## When it applies

(1) Structure and trip count as in scope ([#350](https://github.com/Hawkynt/Polypiler/issues/350)). (2) Length stability: no instruction
of the loop may change the length of, or replace, an array whose check is to be removed ([#114](https://github.com/Hawkynt/Polypiler/issues/114),
[#82](https://github.com/Hawkynt/Polypiler/issues/82) — the rule of [#502](https://github.com/Hawkynt/Polypiler/issues/502)). (3) The guard is the conjunction, over the selected accesses,
of `0 ≤ index(0)`, `index(T − 1) < Length` (mirrored for a negative step) and a no-wrap condition for the
index recurrence over `T` iterations; it is built from comparisons and `And` only, in a width in which its
own arithmetic cannot wrap, and therefore cannot trap. A zero-trip execution may take either version.
(4) Each `BoundsProof` is discharged by [#71](https://github.com/Hawkynt/Polypiler/issues/71) (5 s); on a timeout or a failed obligation that access
stays `[bounds]` in the fast clone, and if no access is left the loop is not versioned. (5) The guard is
neither provably true ([#94](https://github.com/Hawkynt/Polypiler/issues/94) — then [#502](https://github.com/Hawkynt/Polypiler/issues/502) removes the checks without a clone) nor
provably false. (6) The loop has not been versioned before (its preheader does not already end in this
pass's guard). (7) Every instruction of the fallback is the original; the fast clone differs only in the
contracts of the proven accesses. (8) [#155](https://github.com/Hawkynt/Polypiler/issues/155) accepts the duplicated loop for the checks it removes.

## How it is proven

`range-facts+smt-bitvector`: each proof record lists the guard conditions and the recurrence facts it uses and is discharged by the solver; on timeout the check stays (`not applied: proof timeout` when none remains).

## When it must not fire

- `for (i = 0; i <= n; i++) a[i]` guarded by `n < Length` only: for `n = 4294967295` the loop never ends and `i` wraps — the guard needs the no-wrap condition `n < 4294967295`.
- `a[i + 1]` guarded by `n <= Length`: the last index is `n`, one past the end.
- `a[2 * i]` with the guard computed in 32 bits: for `n = 2^31 + 1` the last index `2n − 2` wraps to 0 and passes.
- A body that appends to the list being indexed: the length the guard tested is no longer the length.
- A guard that is false at compile time (`n` proven > Length): the fast clone is dead code that later cleanup removes, and the pass would version the fallback again on the next sweep.

## Targets

Every target on which a bounds check costs something and the backend can omit it for an
`[inbounds]` access — C, C++, Rust and assembly backends, machine code, and managed targets with unchecked
access forms. The fast clone is also what lets [#510](https://github.com/Hawkynt/Polypiler/issues/510) and [#958](https://github.com/Hawkynt/Polypiler/issues/958) fire, which matters on every
target with vectors or threads. The loop exists twice afterwards, in source as in machine code; that is why
the pass is off under `size` and `memory`, and why [#155](https://github.com/Hawkynt/Polypiler/issues/155) ranks `checked` against `guard + unchecked`
(one comparison and branch per removed check and iteration saved, one loop body added), checked first.

## Switching it off

`--disable-opt O0323`; active at `--opt speed` and, when the cost model approves, `balanced`; off at `size`, `memory` and `none`.

## Interactions with other passes

runs in phase `optimize` after [#502](https://github.com/Hawkynt/Polypiler/issues/502), [#508](https://github.com/Hawkynt/Polypiler/issues/508) (invariant lengths exposed) and [#514](https://github.com/Hawkynt/Polypiler/issues/514) (so that the fast clone's removed checks are not mistaken for invariant branches), before [#510](https://github.com/Hawkynt/Polypiler/issues/510), [#513](https://github.com/Hawkynt/Polypiler/issues/513) and `OPT-LOOP-ADDRIV`.
