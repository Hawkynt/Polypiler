# O0002 — Evaluate pure calls with constant arguments at compile time

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#162](https://github.com/Hawkynt/Polypiler/issues/162). Once implemented it is switched off with `--disable-opt O0002`.

## What it does

Algorithms carry code that only computes constants: a CRC table built by a loop at start-up, `factorial(12)`,
a key schedule for a fixed key. Evaluated at compile time they become data — which matters most on targets
where the loop is slow (interpreted BASIC, 6502) or where start-up code is unwelcome. PRD §4.8 ("constant/
partial evaluation"), §8 P1.

## Example

### Before

```
%t = Call.u32 @crc32_table()          // pure, returns a fresh [256 x u32], steps = 256 × 8 loop iterations
%e = Load.u32 %t, 1
```

### After

```
%t = ConstArray.u32 @crc32_table.result   // 256 literals, read-only
%e = Load.u32 %t, 1                       // 0x77073096 after load folding by later passes
```

## When it applies

All of: (1) the call is direct and the callee body is in the module; (2) [#80](https://github.com/Hawkynt/Polypiler/issues/80) reports no
`Memory.Write` outside allocations the callee itself creates, no `Memory.Read` of mutable non-local storage,
no `Device.*`, `External.Call`, `Synchronize`, `Runtime.State` or `Timing` effect, transitively; (3) every
argument is a literal; (4) evaluation by [#119](https://github.com/Hawkynt/Polypiler/issues/119) completes inside the step and memory budgets without a
trap; (5) every floating-point instruction executed is exact-foldable under the [#154](https://github.com/Hawkynt/Polypiler/issues/154) rules;
(6) an array result is not written through, not compared by identity and does not escape to an unknown call
([#121](https://github.com/Hawkynt/Polypiler/issues/121) region facts) — it becomes a read-only `ConstArray`. A scalar result becomes a literal.

## How it is proven

`interpreter+effect-facts`: the result is the callee's own semantics executed on the given arguments; purity and determinism come from [#80](https://github.com/Hawkynt/Polypiler/issues/80). Budget exhaustion leaves the call in place (`not applied: step budget exceeded`).

## When it must not fire

- A callee that reads a mutable global: `counter()` returning `g` then incrementing it elsewhere — the value at compile time is not the value at run time.
- `factorial(13)` with `Mul.i32[checked]`: evaluation traps (13! = 6227020800 > 2^31−1); the trap must happen at run time, so the call stays. With `Mul.i32[wrap]` the result is 1932053504.
- `while (true) {}` in a pure function: the evaluator must stop at the budget, and the non-termination stays in the program.
- A returned array that the caller mutates: sharing one constant array between two call sites would make the second caller see the first one's writes.
- A callee using `Sin.f64`: the host's result may differ from the target's in the last bit.

## Switching it off

`--disable-opt O0002`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` after [#160](https://github.com/Hawkynt/Polypiler/issues/160) has summarized callees and after constant folding; enables load folding from constant arrays, [#156](https://github.com/Hawkynt/Polypiler/issues/156) of the now-unused callee and table recognition.
