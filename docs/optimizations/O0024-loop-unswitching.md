# O0024 — Hoist loop-invariant branches by versioning the loop

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#514](https://github.com/Hawkynt/Polypiler/issues/514). Once implemented it is switched off with `--disable-opt O0024`.

## What it does

`for each pixel: if (bigEndian) … else …`, a mode flag tested per byte, a null check of an optional table
inside the hot loop: the branch outcome is the same in every iteration, but it is paid per element and it
blocks vectorization of both arms. Versioning the loop on the condition leaves two branch-free loops. It
doubles the loop's code, so it is a cost-model decision. PRD §4.8 (cost model: speed vs size).

## Example

### Before

```
loop:                                  // i = 0 .. n-1
  %x = Load.u32[inbounds #1] %a, %i
  Br %flag, add, sub                   // %flag defined before the loop
add: %s1 = Add.u32[wrap] %s, %x ; Br latch
sub: %s2 = Sub.u32[wrap] %s, %x ; Br latch
```

### After

```
pre:
  Br %flag, loop.t, loop.f
loop.t:                                // i = 0 .. n-1
  %x  = Load.u32[inbounds #1] %a, %i
  %s1 = Add.u32[wrap] %s, %x
loop.f:                                // i = 0 .. n-1
  %x' = Load.u32[inbounds #1] %a, %i
  %s2 = Sub.u32[wrap] %s, %x'
```

## When it applies

(1) The condition value is defined outside the loop, or is computed inside it by instructions that
[#508](https://github.com/Hawkynt/Polypiler/issues/508) classifies as hoistable *without* the guaranteed-to-execute exception — i.e. pure and
non-trapping, loads not writable in the loop ([#82](https://github.com/Hawkynt/Polypiler/issues/82)), no effects ([#114](https://github.com/Hawkynt/Polypiler/issues/114)). A condition that
may trap or has effects is not unswitched: testing it before the loop would execute it when the loop runs
zero times or ahead of earlier effects. (2) Evaluating the pure condition once before the loop instead of
per iteration is unobservable. (3) Both copies keep all other instructions in order, so effects and traps
are unchanged. (4) Size: the loop has ≤ 64 instructions, at most two unswitches per original loop, and
[#155](https://github.com/Hawkynt/Polypiler/issues/155) accepts the duplicated size under the objective (never at `size` or `memory`).

## How it is proven

`structural+effect-facts+alias-facts`: for a fixed value of the invariant condition each iteration takes the same arm; specializing the loop per value is case distinction. No solver, no timeout path.

## When it must not fire

- The condition is `Load.u8 %mode` and the loop stores through a pointer that may alias `%mode`: the mode can change mid-loop.
- The condition is `UDiv.u32 %a, %b > 3` with `b` possibly 0 and the loop may run zero times.
- The condition calls a function with effects (a counter, a log): it must run once per iteration.
- A volatile flag polled to stop the loop.

## Switching it off

`--disable-opt O0024`; active at `--opt speed` and, when the cost model approves, `balanced`, off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` after [#508](https://github.com/Hawkynt/Polypiler/issues/508) and before [#510](https://github.com/Hawkynt/Polypiler/issues/510) (each version may now vectorize) and [#513](https://github.com/Hawkynt/Polypiler/issues/513).
