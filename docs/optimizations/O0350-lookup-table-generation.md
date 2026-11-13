# O0350 — Replace calls to a pure function over a small proven domain by a lookup table computed at compile time

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0350`.

## What it does

A pure function of one byte — a scrambler, a gamma curve, a character class — called in a loop computes
the same 256 answers over and over. [#162](https://github.com/Hawkynt/Polypiler/issues/162) evaluates a call only when its argument is a
constant; when the argument is merely *small*, every possible answer can still be computed at compile
time and the call becomes an indexed load. On an 8-bit CPU that is the difference between a routine and
one instruction. PB-Compiler implements it for integer functions (upstream O0332, pass
`LookupTableGeneration`). PRD §4.8 (evaluation: eager or lazy, under the cost model).

## Example

### Before

```
func @scramble(%x: u16) -> u8        // pure: Truncate.u8 (Xor (Mul[wrap] %x, 7), (LShr[strict] %x, 3))
%r1 = Call.u8 @scramble(%a)          // range of %a: [10, 25]
%r2 = Call.u8 @scramble(%b)          // range of %b: [12, 20]
```

### After

```
global @scramble.lut: u8[16] = [71, 76, 85, 90, 99, 104, 114, 117, 124, 135, 142, 145, 152, 163, 171, 172] [readonly, generated=O0350]
%i1 = Sub.u16[wrap] %a, 10
%r1 = Load.u8[inbounds #1] (ElemAddr @scramble.lut, %i1)     // proofs { #1 bounds: "0 <= %i1", "%i1 < 16" }
%i2 = Sub.u16[wrap] %b, 10
%r2 = Load.u8[inbounds #2] (ElemAddr @scramble.lut, %i2)
```

## When it applies

(1) The callee's `FunctionSummary` ([#87](https://github.com/Hawkynt/Polypiler/issues/87)) has `Purity = Pure`, empty `Reads` and `Writes`, and the function carries no `Timing`, `Runtime.State` or `External.Call` effect ([#114](https://github.com/Hawkynt/Polypiler/issues/114)) — a function under a constant-time contract is excluded because a table indexed by a secret leaks it through the cache. (2) It has exactly one parameter, of a type with at most 16 bits, and a scalar result. (3) At least two call sites have a non-constant argument; these are the sites to replace. (4) The domain `D` is the smallest contiguous interval, in the parameter type's own signedness, containing the [#94](https://github.com/Hawkynt/Polypiler/issues/94) range of the argument at every such site; a range known only in the other signedness, or a wrapped range, is replaced by the full type range; `|D| ≤ MaxEntries`, otherwise the rewrite does not fire. (5) *Total on the domain*: evaluating the callee for **every** value of `D` with the evaluator of [#162](https://github.com/Hawkynt/Polypiler/issues/162) completes with a value. A trap for any value (division by zero, `MIN / −1`, a `[strict]` shift out of range, a failed bounds check), an `UndefinedBehaviourReached`, an exhausted step or memory budget, or an operation the evaluator does not fold exactly abandons the table — a fault that would happen at run time is never compiled away, and a value the program could have computed differently is never guessed. (6) The index `argument − lo(D)` is computed in the parameter's type with `[wrap]` and is in `[0, |D|)` by (4); the load carries that as its bounds proof ([#121](https://github.com/Hawkynt/Polypiler/issues/121), [#111](https://github.com/Hawkynt/Polypiler/issues/111)). (7) [#155](https://github.com/Hawkynt/Polypiler/issues/155) prefers the table: `HelperCost(callSites)` for the calls against `|D| × size` bytes of `Cost.Memory` plus one subtract and one load per site.

## How it is proven

`range-facts+effect-facts+evaluation`: a pure deterministic function is its graph; the table is that graph restricted to a domain every replaced argument provably lies in, computed by the reference semantics. No solver; an exhausted budget means the rewrite does not fire.

## When it must not fire

- `f(x) = 100 / x` with arguments in `[0, 15]`: `f(0)` traps `DivideByZero` at run time; a table would have to hold *something* at index 0.
- A constant-time S-box computed with bit slicing so that its timing does not depend on the key byte: as a table, the cache line touched reveals the index.
- A range `[10, 25]` proven for one call site and none for another: the hull is the whole 16-bit type, 65 536 entries, over the limit.
- An argument range known only as unsigned `[65 526, 65 535]` for a signed 16-bit parameter (−10 … −1): indexing with the unsigned view from the signed lower bound reads the wrong entries.
- A function that reads a global the program later changes: not pure, its answers are not constants.
- One dynamic call plus one call with a literal: 256 bytes spent to remove a single call.

## Targets and cost gate

Valuable on every target, most of all on 8- and 16-bit CPUs and generated BASIC, where a call costs tens of cycles and an indexed load one instruction; no target compiler builds a table from a function body. The decision is the cost model's and it differs by objective and profile: under `memory` and `size` the table must be smaller than what it replaces, on a profile where the function inlines to two instructions the calls are cheaper. In generated source the result is a constant array and an index expression, which reads no worse than the call.

## Switching it off

`--disable-opt O0350`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` as a module pass after [#159](https://github.com/Hawkynt/Polypiler/issues/159) (the calls that remain are those not worth inlining) and after range propagation; its tables are inputs to `OPT-MEM-CONSTMERGE` and are skipped by `OPT-MEM-LUTELIM`; enables [#502](https://github.com/Hawkynt/Polypiler/issues/502)-free indexed loads and [#510](https://github.com/Hawkynt/Polypiler/issues/510) gathers where available.
