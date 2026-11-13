# O0172 — Merge remainder, epilogue and peeled iterations into the main iteration domain

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#987](https://github.com/Hawkynt/Polypiler/issues/987). Once implemented it is switched off with `--disable-opt O0172`.

## What it does

An unrolled loop comes with a tail: a second loop, or a few straight-line statements, that process the
`n mod k` elements the main loop left over. Alignment peeling puts such a fragment in front. After the
main loop has been brought back to unit step, the program still says "process 0..m, then m..n" — two
loops where the algorithm has one, and a split point that only made sense for the original unroll
factor. Merging is valid when the fragments run the same body over adjacent ranges with nothing in
between; a tail that does something else at the end of the data (padding, a final partial block) must
stay. PRD §19.3 ("body, remainder and tails"), §19.5 S2 ("peeled/remainder loops"), §24 S4
("peel/epilogue recovery"). The recovered loop is a counted body over an `IterationDomain` of
[#201](https://github.com/Hawkynt/Polypiler/issues/201) (`range(lo, hi, step)`); `PolyIteration` in the listings is PRD §22's name for that
counted body and is spelled as [#201](https://github.com/Hawkynt/Polypiler/issues/201) defines it. The original instructions are kept as the
fallback graph with a `RecoveredFrom` link, and the report says "proven equivalent to", never "intended
as" (PRD §19.1).

## Example

### Before

```
%m = And.u32 %n, 0xFFFFFFFC
%d1 = IterationDomain range(0, %m, 1)
PolyIteration %d1, λ(%i): { %x = Load.u8 src[%i]; %y = Xor.u8 %x, %key; Store.u8 dst[%i], %y }
%d2 = IterationDomain range(%m, %n, 1)
PolyIteration %d2, λ(%i): { %x = Load.u8 src[%i]; %y = Xor.u8 %x, %key; Store.u8 dst[%i], %y }
```

### After

```
%dom = IterationDomain range(0, %n, 1)
PolyIteration %dom, λ(%i): { %x = Load.u8 src[%i]; %y = Xor.u8 %x, %key; Store.u8 dst[%i], %y }
```

## When it applies

(1) the fragments are consecutive in control flow: every path from the end
of one leads to the start of the next with no instruction that has effects or defines values used by
the bodies in between (index arithmetic and the remainder guard are allowed); (2) the bodies are
isomorphic typed SSA graphs under the identity substitution of the index — same operations, same
invariant operands; (3) the domains are adjacent and in order: the second starts exactly where the
first ends, proven as an equality of the bound expressions (`m` and `m`), with the same step; guards
such as `if (m < n)` must be implied by an empty second domain; (4) loop-carried values are threaded:
the initial value of each carried value in the later fragment is the exit value of the same value in
the earlier one; (5) the merged bound does not wrap and equals the end of the last fragment; (6)
because the merged loop executes the same body instances in the same order, no alias, bounds or trap
fact is required beyond those; (7) values that are live between the fragments (`m` itself, the
intermediate accumulator) and used elsewhere stay computed.

## How it is proven

`smt-bitvector` for domain adjacency and the equality of the merged trip count with
the sum of the fragments' trip counts for all `n`; structural isomorphism of the bodies (checked by an
independent graph comparison); `loop-inductive` threading obligation for carried values; bounded
`exhaustive` interpretation for `n` ∈ 0..2k+1. A failed, unknown or timed-out obligation (budget 5 s
each) leaves the IR unchanged and reports `not applied: proof timeout` or the failing precondition.

## When it must not fire

- A tail with a different body: the last partial block is XOR-ed with an extra `0x80`; merging would
  apply the plain body to it.
- A gap: main loop over `[0, m)`, tail over `[m+1, n)` — element `m` is never processed; a merged loop
  would process it.
- An overlap: tail over `[m-1, n)` — element `m-1` is processed twice; with an XOR body the second
  application undoes the first, which a merged loop does not reproduce.
- A restarted accumulator: the tail computes its own sum from 0 and the results are combined later by
  another operation.
- A call between the loops (progress callback, checksum of the partial result): it observes the state
  after exactly `m` elements.

## Switching it off

`--disable-opt O0172`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recover` after [#986](https://github.com/Hawkynt/Polypiler/issues/986) (O0171) and [#985](https://github.com/Hawkynt/Polypiler/issues/985)
(O0170) have produced unit-step fragments; enables the sequence and memory rules on the whole range
and removes the last trace of the source's unroll factor.
