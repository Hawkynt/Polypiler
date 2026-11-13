# O0153 — Rewrite pointer-increment loops as index loops over an iteration domain

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#578](https://github.com/Hawkynt/Polypiler/issues/578). Once implemented it is switched off with `--disable-opt O0153`.

## What it does

C, C++, assembly-flavoured PowerBASIC and every lifted binary walk memory with a moving pointer: `while
(p != end) { *q++ = f(*p++); }`. The loop has no index, no count and two moving addresses, so none of
the map, filter, reduce, copy or fill rules can see it, and a target without pointers cannot express it
at all. Rewriting the pointers as `base + i` over a counted domain is the bridge. The rewrite is only
valid when the pointer provably lands on `end`: a `!=` test that the pointer steps over never terminates
in the source. PRD §4.5 (pointer loops), §4.7 (pointer model), §19.2 ("strength reduction: recover …
from pointer increments").

## Example

### Before

```
loop:
  %p = Phi.ptr [%base, entry], [%p2, body]
  %acc = Phi.u8 [0, entry], [%acc2, body]
  %done = ICmp.eq.ptr %p, %end
  CondBr %done, exit, body
body:
  %x = Load.u8 %p
  %acc2 = Xor.u8 %acc, %x
  %p2 = PtrAdd %p, 1
  Br loop
exit:
  Ret %acc
```

### After

```
entry:
  %d = PtrDiff %end, %base
  Br loop
loop:
  %i = Phi.usize [0, entry], [%i2, body]
  %acc = Phi.u8 [0, entry], [%acc2, body]
  %done = ICmp.eq.usize %i, %d
  CondBr %done, exit, body
body:
  %pi = ElementAddr %base, %i
  %x = Load.u8 %pi
  %acc2 = Xor.u8 %acc, %x
  %i2 = Add.usize %i, 1
  Br loop
exit:
  Ret %acc
```

## When it applies

(1) each moving pointer has the recurrence `p_{k+1} = p_k + s` with a
constant non-zero `s` on every path through the body ([#88](https://github.com/Hawkynt/Polypiler/issues/88)'s scalar evolution, reached
through the loop-shape matcher); (2) for an end-pointer test, `base` and `end` have the same
allocation provenance ([#93](https://github.com/Hawkynt/Polypiler/issues/93)) and `end` lies in the direction of travel at a distance
that is a non-negative multiple of `|s|` — proven by alignment or range facts — when the test is `!=`;
for `<`/`>` tests the count is the distance divided by `|s|` rounded up and only the provenance and
direction are required; (3) for a down-counter the count is the counter's initial value under its own
wrap rules; (4) the moving pointers are not stored, passed to calls or otherwise escape in the loop,
and their final values, if used after the loop, are recomputed as `base + count·s`; (5) the count fits
the index type. Nothing about the body's effects is required: the body is kept as it is.

## How it is proven

`loop-inductive`: the invariant `p = base + i·s` for every moving pointer, and
equality of the exit conditions, discharged by SMT on one symbolic iteration under the provenance and
divisibility facts (`RangeFact`, `AliasFact` obligations); bounded `exhaustive` interpretation for
distances 0..4 elements. A failed, unknown or timed-out obligation (budget 5 s each) leaves the IR
unchanged and reports `not applied: proof timeout` or the failing precondition.

## When it must not fire

- Misaligned end: step 2, `end = base + 3`, test `p != end`: the pointer takes the values `base`,
  `base+2`, `base+4`, … and never equals `end`; the source loop runs off the allocation. A count of `3
  / 2 = 1` would silently terminate it.
- End from another allocation: the distance between unrelated objects is not defined by the source
  language; no count exists.
- Conditional advance (`if (*p) p++`): not an affine recurrence — the loop may stall.
- A pointer that escapes (`save = p` inside the loop): its per-iteration value is observable and must
  be materialized, which is allowed, but a pointer *modified* through that alias is not affine any
  more.
- Down-counter starting at 0 with `while (n--)` on an unsigned type: zero iterations, not `2^w`; with
  `do { } while (--n)` it is `2^w` iterations — the two spellings have different counts.

## Switching it off

`--disable-opt O0153`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` after provenance and scalar-evolution analysis and before
every loop rule of this area (it has the highest need to run first, so the engine's sweep reaches the
other rules in the next sweep); enables [#273](https://github.com/Hawkynt/Polypiler/issues/273), [#277](https://github.com/Hawkynt/Polypiler/issues/277), [#281](https://github.com/Hawkynt/Polypiler/issues/281),
[#282](https://github.com/Hawkynt/Polypiler/issues/282) on pointer code.
