# O0025 — Turn self tail calls and accumulating recursion into loops

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#512](https://github.com/Hawkynt/Polypiler/issues/512). Once implemented it is switched off with `--disable-opt O0025`.

## What it does

Functional-style sources recurse where imperative ones loop: `gcd(b, a % b)`, a list walk, `n * fact(n − 1)`.
Several targets cannot run that at all — the 6502 has a 256-byte stack, classic BASIC dialects have no
recursive procedures, and neither JavaScript engines nor the JVM guarantee tail calls. Turning the recursion
into a loop is therefore closer to legalization than to tuning. PRD §4.8 (required passes are a minimum;
this one is needed by the retro and BASIC targets of §8 P5 and by Kotlin/Rust iterator code in P3–P4).

## Example

### Before

```
@gcd(%a: u32, %b: u32) -> u32:
entry:
  %z = ICmp.eq.u32 %b, 0
  Br %z, done, rec
rec:
  %m = URem.u32 %a, %b
  %r = Call.u32 @gcd(%b, %m)
  Ret.u32 %r
done:
  Ret.u32 %a
```

### After

```
@gcd(%a0: u32, %b0: u32) -> u32:
head:
  %a = Phi.u32 [entry: %a0], [rec: %b]
  %b = Phi.u32 [entry: %b0], [rec: %m]
  %z = ICmp.eq.u32 %b, 0
  Br %z, done, rec
rec:
  %m = URem.u32 %a, %b
  Br head
done:
  Ret.u32 %a
```

## When it applies

(1) The call is a direct call to the enclosing function. (2) Tail form: the block ends `Ret.<T> %r` with `%r` the
call result and no instruction with an effect between call and return; accumulator form: exactly one
operation `⊕` from the list combines the call result with a value computed before the call, in the same
integer type and `[wrap]` contract ([#118](https://github.com/Hawkynt/Polypiler/issues/118)), and its result is returned. (3) The call is not inside an
exception region whose handler lies in this function ([#115](https://github.com/Hawkynt/Polypiler/issues/115)): each frame would have installed its
own handler. (4) No address of a local or parameter slot of the current activation is live across the call
([#114](https://github.com/Hawkynt/Polypiler/issues/114) `Stack`): after the rewrite all "frames" share one slot. (5) Arguments are evaluated before
the parameters are overwritten (parallel assignment through phis). (6) The profile does not declare stack
depth observable (stack-overflow exceptions that the program catches, stack traces printed by the
program); if it does, the rewrite is refused. (7) For the accumulator form the base-case return value `v`
becomes `acc ⊕ v` with `acc` initialized to the operation's identity (0 for `Add`/`Or`/`Xor`, 1 for `Mul`,
all-ones for `And`).

## How it is proven

`structural` for the tail form; `exhaustive-8bit+smt-bitvector` for associativity and commutativity of each accumulator operation at 8 bit (exhaustive) and 16/32/64 bit ([#71](https://github.com/Hawkynt/Polypiler/issues/71), 5 s budget; timeout removes that operation, `not applied: proof timeout`).

## When it must not fire

- `return 1.0 + f(n − 1)` in `f64`: the recursion adds from the innermost call outward, the accumulator from the outermost inward; floating-point addition is not associative.
- `return Mul.i32[checked] n, f(n − 1)`: with a factor 0 at the bottom the recursion multiplies `0 × …` and never overflows, the accumulator overflows on the way down (e.g. `n = 20`: 20 × 19 × … exceeds 2^31 before reaching 0).
- `return Sub.u32 n, f(n − 1)`: subtraction is not associative — `3 − (2 − (1 − 0)) = 2`, an accumulator computes `((3 − 2) − 1) − 0 = 0`.
- The recursive call sits in `try { return f(n − 1); } catch`: an exception from depth 5 must unwind through five handlers.
- A local's address passed down (`f(&local)`): every level must have its own `local`.

## Switching it off

`--disable-opt O0025`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` before [#159](https://github.com/Hawkynt/Polypiler/issues/159) considers the function (a loop is inlinable, a recursive function is not); enables [#508](https://github.com/Hawkynt/Polypiler/issues/508), [#513](https://github.com/Hawkynt/Polypiler/issues/513), [#510](https://github.com/Hawkynt/Polypiler/issues/510) and [#956](https://github.com/Hawkynt/Polypiler/issues/956) stack ceilings.
