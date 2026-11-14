# Proof requirements of a permutation network

Normative text for every grammar under `spec/permutations/grammars/`. The key words MUST, MUST NOT,
SHOULD and MAY are used as in RFC 2119. A *network* is a sequence of primitives drawn from one grammar;
a *mapping* is a document under `spec/permutations/mappings/` or the `BitMapping` it describes.

## Equivalence

1. A network is equivalent to a mapping when, for every input `x` in the mapping's input condition, the
   network's output and the mapping's output agree on every bit of the mapping's `RequiredOutputMask`.
   The input condition is the domain of the `BitCondition` the mapping is checked under: every `x` with
   `x & InputKnownZero == 0` and `x & InputKnownOne == InputKnownOne`; without a stated condition it is
   every input of `inputWidth` bits.
2. Output bits outside `RequiredOutputMask` (the mapping's don't-cares) MUST NOT be compared. Inputs
   outside the input condition MUST NOT be used as counterexamples.
3. Equivalence is a property of the exact network. A network MUST be evaluated with the true semantics of
   each primitive at the mapping's width, never with a model of where its bits may land.

## Proof by width

4. For an input width of 16 bits or less the proof MUST be exhaustive: every input of the input condition
   is evaluated, and the proof records the number of cases.
5. For an input width above 16 bits the proof MUST be discharged by an SMT solver over bit-vectors of the
   mapping's width; the proof records the solver and its result. Sampled inputs, test vectors included,
   MAY reject a network but MUST NOT accept one.

## Multiplication

6. A network that contains a `multiply` step MUST pass the exact-arithmetic check of #531: the product is
   computed modulo `2^width` with every carry propagated. The possible-landing analysis of a multiply
   routing MAY rule a routing out but MUST NOT prove one; a routing is accepted only when its columns are
   shown carry-free or the exact check above succeeds.

## Optimality labels

7. A result found under a grammar carries exactly one of three labels (#973); the word "optimal" alone
   MUST NOT be used.
   - `heuristic`: the network was found by a strategy that does not explore its grammar completely
     (greedy, butterfly, Beneš). Nothing is claimed beyond equivalence.
   - `best-found`: the network is the cheapest one a complete strategy found before its search budget
     ran out. The report MUST state the budget and that it was exhausted.
   - `optimal-under-grammar`: a complete strategy explored the grammar within its `maxDepth` without
     exhausting its budget, under a named cost document. The report MUST name the grammar's spec ID, the
     cost document's spec ID and the search bounds; the claim holds for that grammar and those costs only.
8. Every label requires the equivalence proof of rules 1 to 6. A network without an accepted proof MUST
   NOT be reported as chosen, whatever its label.
