# O0355 — Reassociate wrapping integer chains so that equal sums and products meet and constants fold

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0355`.

## What it does

`x + y + 1` and `1 + y + x` are the same value and two different trees, so [#158](https://github.com/Hawkynt/Polypiler/issues/158) computes both; it
normalizes the operands of one instruction, not the shape of a chain, and names re-association as not
planned. `(x + 5) + 7` is `x + 12`, but [#154](https://github.com/Hawkynt/Polypiler/issues/154) sees no instruction with two constant
operands. Wrapping integer `+`, `×`, `and`, `or`, `xor` are associative and commutative, so chains may
be compared as multisets. PB-Compiler does this before value numbering (upstream O0061, pass
`Reassociate`). PRD §4.8 (redundancy elimination), §4.1 (never for floats without proof).

## Example

### Before

```
%t1 = Add.u32[wrap] %x, %y
%a  = Add.u32[wrap] %t1, 1
%t2 = Add.u32[wrap] 1, %y
%b  = Add.u32[wrap] %t2, %x          // same multiset {x, y, 1}

%u1 = Add.u8[wrap] %z, 200
%u  = Add.u8[wrap] %u1, 100
```

### After

```
%t1 = Add.u32[wrap] %x, %y
%a  = Add.u32[wrap] %t1, 1           // every use of %b now uses %a; %t2 is dead

%u  = Add.u8[wrap] %z, 44            // (200 + 100) mod 256
```

## When it applies

(1) Every node of the tree has the same opcode, the same integer type and — for `Add` and `Mul` — the `[wrap]` contract ([#118](https://github.com/Hawkynt/Polypiler/issues/118), [#122](https://github.com/Hawkynt/Polypiler/issues/122)); a node with another contract, another type or a conversion is a leaf. (2) The key of a tree is its opcode, its type, the folded constant (computed with [#154](https://github.com/Hawkynt/Polypiler/issues/154)'s evaluator at the tree's width) and the non-constant leaves sorted by their definition index in the function (parameters first, then instructions in reverse post-order) with multiplicity. (3) R2 replaces root `S` by root `R` only if `R` dominates `S` and both keys are equal ([#128](https://github.com/Hawkynt/Polypiler/issues/128)); no instruction is created or moved. (4) R1 rebuilds a tree only if every interior node has a single use — otherwise the old nodes stay alive and the rewrite would add work; the rebuilt chain keeps the non-constant leaves in their original left-to-right order and appends the constant; an `Add` whose folded constant is 0, a `Mul` whose folded constant is 1, an `And` with all ones, an `Or`/`Xor` with 0 drop the constant. (5) All operations are pure; nothing in this pass can add or remove a trap, because only `[wrap]` and bitwise nodes are touched. (6) The identities used are proven for the opcode and width before the first rewrite at that width.

## How it is proven

`exhaustive-8bit+smt-bitvector+structural`: associativity and commutativity of each opcode are checked over all 2^24 operand triples at 8 bits and by SMT at 16, 32 and 64 bits; equality of two trees with equal leaf multisets follows by induction. A solver timeout (5 s per obligation) disables the rewrite for that opcode and width and reports `not applied: proof timeout`.

## When it must not fire

- Checked `i8`: `(x + 100) + (−100)` with `x = 100` traps at the first addition; `x + 0` does not.
- Saturating `i8`: `(127 + 1) + (−1)` is `127 + (−1) = 126`; `127 + (1 + (−1))` is 127.
- Across a conversion: `ZeroExtend.u32 (Add.u8[wrap] a, b) + c` with `a = 200`, `b = 100`, `c = 1` is 45; `ZeroExtend(a) + ZeroExtend(b) + c` is 301.
- C signed arithmetic (`[undefined]`): `(a + b) + c` with `a = INT_MAX`, `b = −1`, `c = 1` is defined; regrouped as `(a + c) + b` it overflows. A pass may not introduce that.
- Floats: `(0.1 + 0.2) + 0.3` is 0.6000000000000001 and `0.1 + (0.2 + 0.3)` is 0.6.
- Subtraction is not commutative: only `x − c` with a constant is read as `x + (−c)`; `a − b` and `b − a` never meet.

## Targets and cost gate

Every target. R2 removes a computation and R1 removes one operation per folded constant; no rule adds an instruction, so the cost model is not consulted. A tree that gains nothing is left exactly as the source wrote it, so generated source never shows operands shuffled for the compiler's convenience — the one deliberate difference from upstream, which canonicalizes every tree because its output is machine code. Target compilers reassociate integers themselves; doing it here still pays, because [#158](https://github.com/Hawkynt/Polypiler/issues/158), [#508](https://github.com/Hawkynt/Polypiler/issues/508) and the recognition rules run on PolyIR.

## Switching it off

`--disable-opt O0355`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` after [#164](https://github.com/Hawkynt/Polypiler/issues/164) and immediately before [#158](https://github.com/Hawkynt/Polypiler/issues/158); enables [#158](https://github.com/Hawkynt/Polypiler/issues/158) (R2 feeds it equal values), [#156](https://github.com/Hawkynt/Polypiler/issues/156) (the dead duplicate chain), [#508](https://github.com/Hawkynt/Polypiler/issues/508) and `OPT-RED-TREEBALANCE`, which reuses the flattening.
