# O0131 — Recognize widening multiplication and multiply-high from half-width partial products

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#581](https://github.com/Hawkynt/Polypiler/issues/581). Once implemented it is switched off with `--disable-opt O0131`.

## What it does

Where the language has no double-width product, the high half is assembled from four products of
half-words with a hand-carried middle column — Cipher's `MulHi32` does exactly this for JavaScript, and
Argon2, Philox and MWC generators depend on it. x86 returns the high half in `EDX` for free, ARM has
`UMULH`, C# has `Math.BigMul`. The middle-column carry is the part that is easy to get wrong and easy to
overlook: without it the result is off by one for a quarter of all inputs. PRD §19.2 ("high/low
multiply"), §23.4.

## Example

### Before

```
%al = And.u32 %a, 0xFFFF
%ah = LShr.u32[strict] %a, 16
%bl = And.u32 %b, 0xFFFF
%bh = LShr.u32[strict] %b, 16
%p0 = Mul.u32 %al, %bl
%p1 = Mul.u32 %al, %bh
%p2 = Mul.u32 %ah, %bl
%p3 = Mul.u32 %ah, %bh
%c0 = LShr.u32[strict] %p0, 16
%c1 = And.u32 %p1, 0xFFFF
%c2 = And.u32 %p2, 0xFFFF
%c3 = Add.u32 %c0, %c1
%c4 = Add.u32 %c3, %c2
%carry = LShr.u32[strict] %c4, 16
%h1 = LShr.u32[strict] %p1, 16
%h2 = LShr.u32[strict] %p2, 16
%s1 = Add.u32 %p3, %h1
%s2 = Add.u32 %s1, %h2
%hi = Add.u32 %s2, %carry
```

### After

```
%hi = MulHigh.u32 %a, %b
```

## When it applies

structural gate: a pure DAG over exactly two leaves `a`, `b` of type
`u<w>`, in which each leaf is split into its halves by `And` with `2^h - 1` and `LShr` by `h`, all
four cross products occur as `Mul`, and the remaining operators are `Add`, `LShr`, `Shl`, `And` with
the half mask. Decision at `w = 16`: SMT equivalence with `Trunc(LShr(Mul.u32(ZExt a, ZExt b), 16))`.
At `w = 32` and `64` the SMT obligation is attempted with the usual budget; on timeout the rule falls
back to the *schema check*: the DAG must be the `h`-scaled image of a DAG that was proven at `h = 4`
(exhaustively) and `h = 8` (SMT), i.e. identical after replacing the constants `h` and `2^h - 1` — a
parametric rule with checked preconditions. No intermediate may wrap: this follows from the schema
(each column sum is below `3·2^h`).

## How it is proven

`smt-bitvector` at 16 bit; at 32/64 bit `smt-bitvector` when the solver answers in
budget, otherwise `validated-rule+differential` (schema proven at `h = 4` exhaustively and `h = 8` by
SMT, plus the differential run with forced boundary operands). The report names which of the two
applied. A failed, unknown or timed-out obligation (budget 5 s each) leaves the IR unchanged and
reports `not applied: proof timeout` or the failing precondition.

## When it must not fire

- Missing middle carry: `a = b = 0xFFFFFFFF` gives `0xFFFFFFFD` without the carry term; the true high
  word is `0xFFFFFFFE`. Also `a = 0x12345678`, `b = 0x9ABCDEF0`: `0x0B00EA4D` against `0x0B00EA4E`.
- Arithmetic shift for the high half: `ah = AShr(a, 16)` makes the form a mixed signed/unsigned
  product; for `a ≥ 2^31` the result differs.
- Masks of the wrong width (`0xFF` halves on a 32-bit value): the upper bits of each half are dropped.
- One cross product missing or doubled (`al·bh` twice): only correct when `a = b`.

## Switching it off

`--disable-opt O0131`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` after [#560](https://github.com/Hawkynt/Polypiler/issues/560) (O0130) declined the column sums
(they are not limb carries) and before [#565](https://github.com/Hawkynt/Polypiler/issues/565) (O0126), which consumes `MulHigh`; enables
single-instruction widening multiply.
