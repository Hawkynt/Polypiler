# O0373 — Replace a transcendental call over a proven finite integer domain by a table of its reference results

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0373`.

## What it does

`sin(a * PI / 128)` for a byte `a` is the classic sine table computed the slow way: on a target where `Sin` is a
generated software routine, every call costs thousands of cycles, yet the argument takes only 256 values. The
call can become a load from a 256-entry table — provided each entry is the value the program would have
computed. That holds exactly where the target runs Polypiler's own helper, which [#650](https://github.com/Hawkynt/Polypiler/issues/650) keeps
bit-identical to the reference evaluator; where the target calls its own library the table would change bits,
which needs a licence nothing grants today. [#648](https://github.com/Hawkynt/Polypiler/issues/648) leaves table generation to "an explicit approximate
mode"; this issue delivers the part that needs none. PRD §4.8, §17.1.

## Example

### Before

```
%f = IntToFloat.f64[rne] %a                  // %a: u8
%t = FMul.f64[rne] %f, 0.02454369260617026   // PI / 128
%s = Sin.f64 %t
```

### After

```
global @f$sin.tab: f64[256] = [0.0, …256 reference results…] [readonly]

%j = ZExt.uptr %a
%s = Load.f64[inbounds #1] @f$sin.tab, %j
```

## When it applies

(1) The operation is a registered one-argument tolerance operation at `f32` or `f64` without a trap
contract. (2) Its argument is a pure function of one integer SSA value `i`: `IntToFloat` of `i` (or of an
exact integer expression of `i`) followed only by operations [#154](https://github.com/Hawkynt/Polypiler/issues/154) marks exact-foldable, with
literal other operands. (3) [#94](https://github.com/Hawkynt/Polypiler/issues/94) gives `i` a range `[lo, hi]` at the call with `hi − lo + 1 ≤ 256`.
(4) Numeric contract — one of: (a) the target's cost profile lists the operation as `helper`, so the program
would run the generated helper, which is bit-identical to the reference evaluator; (b) the operation has
`accuracy=cr` and a correctly rounded reference is registered for it. In every other case the rewrite does
not fire: under `ulp(n)` with a native library, and under `library(<name>)`, the table would return other bits
than the call. (5) Every entry is the evaluator's result for the argument chain interpreted at that value of
`i`; no host library function is called. (6) The load carries a bounds proof from the range of `i`. (7)
[#155](https://github.com/Hawkynt/Polypiler/issues/155) scores table plus load strictly below the call under the active objective; the call is listed
first.

## How it is proven

`interpreter+range-facts`: each entry is the operation's own reference semantics at one point of a domain the range fact proves complete; the load selects the entry of the current `i`. No solver, no timeout path.

## When it must not fire

- A target that maps `Sin.f64` to its C library: the library result and the reference result may differ by an ULP inside the contract; a table of reference values changes program output.
- An argument known only to lie in `[0.0, 1.0]`: the interval contains about 2^62 doubles.
- An index with 257 possible values, or a range that holds only on one branch arm while the call is outside it.
- An argument built from two integers (`i * j`): the domain is the product of both ranges.
- Entries computed with the host's `Math.Sin`: bits depend on the machine that compiled.

## Targets

only targets on which the operation is a generated helper: CPUs without a floating-point unit and
runtimes without a math library (6502, Z80, 8086 without 8087, freestanding C). There a 2 048-byte table
replaces a software `Sin` per call; `--opt memory` and `--opt size` weigh the table against the call. On
targets with a native math library the default behaviour is that the rewrite does not fire, so generated C#,
Rust or JavaScript keeps calling its library function.

## Switching it off

`--disable-opt O0373`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`; subject to the numeric-contract gate and the cost decision.

## Interactions with other passes

runs in phase `specialize`, where the target profile is known, after range analysis is final; enables `OPT-IPA-CONSTGLOBAL`-style folding of loads with a literal index and removes the helper from the closure when no call remains.
