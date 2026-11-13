# O0173 — Slice interleaved arithmetic into independent dependence chains

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#981](https://github.com/Hawkynt/Polypiler/issues/981). Once implemented it is switched off with `--disable-opt O0173`.

## What it does

Schedulers interleave independent calculations to hide instruction latency: `a1 = a0 << 2; b1 = b0 << 2;
a2 = a1 + a0; b2 = b1 + b0; a3 = a2 << 1; b3 = b2 << 1`. In lifted machine code the interleaving is
everywhere, and it additionally threads flags and reused registers through unrelated work. Read in
order, the block is noise; sliced along its dependences it is two copies of `x * 10`. Slicing is the
step that turns register scheduling back into expressions, and it is safe exactly as far as the
dependences — data, flags, memory, traps — are complete. PRD §19.2 (graph level), §19.3 (interleaving),
§19.5 S3 ("preserve demanded flags"), §22 ("Recover multiplication from interleaved arithmetic"), §24
S3. The original instructions are kept as the fallback graph with a `RecoveredFrom` link, and the report
says "proven equivalent to", never "intended as" (PRD §19.1).

## Example

### Before

```
%a1 = Shl.u32[strict] %a0, 2
%b1 = Shl.u32[strict] %b0, 2
%a2 = Add.u32 %a1, %a0
%b2 = Add.u32 %b1, %b0
%a3 = Shl.u32[strict] %a2, 1
%b3 = Shl.u32[strict] %b2, 1
```

### After

```
%a1 = Shl.u32[strict] %a0, 2          ; chain 1
%a2 = Add.u32 %a1, %a0
%a3 = Shl.u32[strict] %a2, 1
%b1 = Shl.u32[strict] %b0, 2          ; chain 2, same signature as chain 1
%b2 = Add.u32 %b1, %b0
%b3 = Shl.u32[strict] %b2, 1
; with O0125 enabled afterwards:  %a3 = Mul.u32 %a0, 10   and   %b3 = Mul.u32 %b0, 10
```

## When it applies

(1) the new order is a topological order of the *complete* dependence
graph of the block: SSA def-use edges; flag and register edges for machine-level instructions — every
flag an instruction reads links it to the instruction that defined it; memory edges — every load
depends on each earlier store that [#82](https://github.com/Hawkynt/Polypiler/issues/82) cannot prove disjoint, every store on earlier loads
and stores likewise; trap and effect edges — instructions that may trap, calls, volatile and device
accesses keep their relative order with respect to each other and to every store; (2) only
instructions that are pure, cannot trap and are not ordered by an effect edge are moved; everything
else stays at its position relative to the other fixed instructions; (3) a flag definition that no
instruction demands is not an edge (demanded-flags analysis), a flag definition that is demanded is
kept with its consumer in one chain; (4) the block's terminator and phi nodes are not moved; (5)
nothing is deleted or rewritten by this rule itself.

## How it is proven

structural with an independent checker: a dependence-preserving permutation of pure
instructions is semantics-preserving by construction; a separate component (not the slicer) recomputes
the dependence graph and verifies that the new order is one of its topological orders — a
`ValidatedRule` obligation — and `smt-bitvector` translation validation of the block's live-out values
on symbolic inputs confirms it for blocks of up to 64 instructions; `exhaustive-8bit` on the narrowed
fixture. A failed, unknown or timed-out obligation (budget 5 s each) leaves the IR unchanged and
reports `not applied: proof timeout` or the failing precondition.

## When it must not fire

- Flags: `ADD` in chain A sets carry, `ADC` in chain B consumes it. Treating flags as non-values would
  separate them and let another flag-setting instruction slide in between.
- Trap order: `%q = UDiv %x, %y` followed by `Store %p, %v`. Moving the store's chain in front makes
  the store happen before a division-by-zero trap that originally preceded it.
- Memory order: `Store %p, 1` in chain A, then `%v = Load %q` in chain B with `q` possibly equal to
  `p`. Hoisting the load above the store reads the old value.
- A volatile read in each chain: their relative order is an observable bus sequence.
- A dead flag definition taken as a dependence would chain everything to everything and hide the
  independence — sound but useless; the rule must separate demanded from dead flags to find the chains
  at all.

## Switching it off

`--disable-opt O0173`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recover` after flag-liveness and alias analysis; then requests a
`recognize` sweep through `PassContext.RequestSweep`, where [#572](https://github.com/Hawkynt/Polypiler/issues/572) and the other expression
rules see each chain; provides the chain metadata that [#979](https://github.com/Hawkynt/Polypiler/issues/979) consumes.
