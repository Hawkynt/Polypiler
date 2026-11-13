# O0424 — Test membership in a small constant set with one shift and mask

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0424`.

## What it does

`k = 1 OR k = 3 OR k = 5 OR k = 9` and a `CASE 1, 3, 5, 9` arm ask whether a value is in a small
constant set. As a compare chain that is one comparison per member; as a bit mask it is a range check, a
shift and a test, whatever the size of the set. PB-Compiler lowers both spellings to the mask (PB O0099).
In the IR both spellings are a `Switch` with one arm block once `OPT-LAYOUT-IFCHAIN` has run, and the
mask form is ordinary integer arithmetic whose equivalence can be proven exhaustively. PRD §4.8.

## Example

### Before

```
entry:
  Switch.i16 %k, no, [1: yes], [3: yes], [5: yes], [9: yes]
```

### After

```
entry:
  %k.idx = Sub.i16[wrap] %k, 1
  %k.in = ICmp.ule.i16 %k.idx, 8
  CondBr %k.in, entry.bit, no
entry.bit:
  %k.u = BitCast.u16 %k.idx
  %k.sh = LShr.u16[strict] 277, %k.u          // 0x0115: bits 0, 2, 4, 8
  %k.b = And.u16 %k.sh, 1
  %k.m = ICmp.ne.u16 %k.b, 0
  CondBr %k.m, yes, no
```

## When it applies

With `N` the selector width, `min`/`max` the smallest and largest member and `window = max − min`: (1) every case of the `Switch` has the same target, which differs from the default; (2) at least `MinMembers = 3` members; (3) `window ≤ N − 1`; (4) the mask is the `u<N>` constant with bit `m − min` set for every member `m`; (5) the range check is the *unsigned* comparison `(k − min) ≤ window` at the selector's full width, and the shift is `[strict]`, which the check makes legal ([#94](https://github.com/Hawkynt/Polypiler/issues/94) refines the index to `[0, window]` in `entry.bit`); (6) phis of the two targets get the original block's input for each new predecessor; (7) the profile has a `dispatch` section and [#155](https://github.com/Hawkynt/Polypiler/issues/155) chooses `bittest` = `Of(Sub) + 2 × Of(CondBr) + Of(LShr.u<N>) + Of(And.u<N>)` (no `Sub` when `min = 0`).

## How it is proven

`exhaustive-8bit` over the whole rule family and `exhaustive-16bit` on the fixtures; `smt-bitvector` per instance at 32 and 64 bit with a 5 s budget — on timeout the `Switch` is left to the other strategies (`not applied: proof timeout`).

## When it must not fire

- No range check, `k = 17` with members {1, 3, 5, 9}: the index is 16; a 16-bit shift by 16 is undefined under `[strict]`, and an x86 that masks the count shifts by 0 and reports a member.
- A signed range check: `k = 0` gives index −1, which is ≤ 8 signed; the shift amount is 65 535.
- Members {0, 16} and one more on a 16-bit selector: the window is 16, bit 16 does not exist in the mask.
- A 32-bit selector tested through its low 16 bits: `0x00010005` reads as 5 and is reported as a member (upstream's `LONG` subject guard).
- The mask built without normalizing: members {100, 102, 105} need bits 100…105.

## Switching it off

`--disable-opt O0424`; active at `--opt balanced`, `speed`, `size` and `memory` when the cost model chooses the bit test, off at `--opt none`.

## Targets

only targets whose multi-way branches Polypiler lowers itself, which a target declares by a `dispatch` section in its `cost.yaml` (`OPT-LAYOUT-JUMPTABLE`): assembly and machine-code backends (x86 and x64 NASM, ARM, 6502, Z80) and source targets without an optimizing compiler behind them whose profile asks for it. A source target with a mature compiler — C, C++, C#, Rust, Go, Java, Kotlin — has no `dispatch` section, keeps the plain `Switch` and prints it as its own `switch`/`match`; nothing in its output changes. Real-mode x86 through [#1046](https://github.com/Hawkynt/Polypiler/issues/1046) is excluded as well: PB-Compiler's own selector chooses the dispatch there. The cost model also keeps it off machine targets without a cheap variable shift: on a 6502 or 8080 `LShr` by a variable amount is a loop or a helper, and the chain wins. For a source target that opts in the result stays structured: the two tests share the non-member target and the second block is pure, which [#262](https://github.com/Hawkynt/Polypiler/issues/262) reports as one `ShortCircuit` region.

## Interactions with other passes

a strategy of `DispatchSelection` (`OPT-LAYOUT-JUMPTABLE`), phase `legalize`; needs the single-arm `Switch` that `OPT-LAYOUT-IFCHAIN` forms from `Or` chains. Changes the CFG and invalidates it.
