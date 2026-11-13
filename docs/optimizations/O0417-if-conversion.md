# O0417 — Turn diamonds and triangles with empty arms into select values

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0417`.

## What it does

`if c then x = a else x = b` reaches the optimizer as a diamond: two empty blocks and a phi. Kept as
control flow it costs two branches and a join, hides `min`, `max` and clamp from the recognition rules, and
blocks vectorization of the surrounding loop. [#571](https://github.com/Hawkynt/Polypiler/issues/571) already assumes that "if-conversion produces
the `Select` this rule matches" and that it runs before recognition, but no planned issue delivers it.
PB-Compiler's if-conversion (PB O0051) is the conservative form that is always legal: only arms that are
empty are converted, so nothing is ever speculated. PRD §4.8, §19.3.

## Example

### Before

```
entry:
  %c = ICmp.sgt.i16 %k, 0
  CondBr %c, then, else
then:
  Br join
else:
  Br join
join:
  %x = Phi.i16 [then: %a], [else: %b]
  Ret.i16 %x
```

### After

```
entry:
  %c = ICmp.sgt.i16 %k, 0
  %x = Select.i16 %c, %a, %b
  Br join
join:
  Ret.i16 %x
```

## When it applies

(1) `B` ends in `CondBr c, T, E` with `T ≠ E`. (2) Each arm is either a block whose only instruction is `Br M` and whose only predecessor is `B`, or the edge to `M` itself; at most one arm is the direct edge. (3) `M` has exactly the two arm ends as predecessors. (4) No phi input over the two edges is `Undef`: the branch never read it, the `Select` would ([#119](https://github.com/Hawkynt/Polypiler/issues/119) aborts with PLY0120). (5) Neither arm block nor `M` is an unwind target ([#115](https://github.com/Hawkynt/Polypiler/issues/115)). (6) [#155](https://github.com/Hawkynt/Polypiler/issues/155) prefers `n × Of(Select.<type>)` over `Of(CondBr) + Of(Br)` for the `n` phis of `M`; on a tie the branch stays. Each phi `Phi [T-side: a], [E-side: b]` becomes `Select c, a, b` inserted before the terminator of `B`, in phi order; `B` then ends in `Br M` and the arm blocks are deleted. All operands were already defined when `B` branched, so no computation is added or moved.

## How it is proven

`structural`: `Select c, a, b` is by definition the value the phi had after a branch on `c` with empty arms. No solver, no timeout path.

## When it must not fire

- `if b <> 0 then q = a \ b else q = 0`: the arm holds a division; computing it before the test traps for `b = 0`.
- `if p <> null then v = p[0] else v = 0`: the guarded load reads through a null pointer when speculated.
- A third predecessor of `join` carrying its own value: the `Select` in `entry` would be executed only on two of the three paths, but replace the phi on all.
- An arithmetic blend `c·a + (1−c)·b` instead of `Select`: with `a = +∞`, `c = 0` it yields NaN, and it loses the sign of `−0.0`. Only `Select`, which copies one operand unchanged, is emitted.
- A phi input that is `Undef` on the path the program never took for that value.

## Switching it off

`--disable-opt O0417`; active at `--opt balanced`, `speed`, `size` and `memory` when the cost model prefers the `Select`, off at `--opt none`.

## Targets

every target that has a cheap conditional value: a ternary or `if`-expression in source languages, `CMOVcc`, `SETcc` and `CSEL` in machine code — there it removes two branches and reads as one expression. It must stay off where a `Select` is itself a branch sequence — 6502, Z80, 8080, tokenized BASIC, classic PowerBASIC without `IIF` — because [#1069](https://github.com/Hawkynt/Polypiler/issues/1069) would only rebuild the diamond. The gate is the target's `cost.yaml` entry for `Select.<type>` against `CondBr` and `Br`; no pass constant decides. The rewrite only removes a diamond or a triangle, so a function [#262](https://github.com/Hawkynt/Polypiler/issues/262) could structure before can still be structured.

## Interactions with other passes

runs in phase `optimize` after `OPT-LAYOUT-SIMPLIFYCFG` (arms emptied by other passes are recognized as empty only after forwarding and merging) and calls `RequestSweep(PassPhase.Recognize)` when it changed a function, so that [#571](https://github.com/Hawkynt/Polypiler/issues/571) and the clamp rules see the `Select`; enables the merge of `B` and `M` by `OPT-LAYOUT-SIMPLIFYCFG` and loop vectorization of branch-free bodies ([#510](https://github.com/Hawkynt/Polypiler/issues/510)).
