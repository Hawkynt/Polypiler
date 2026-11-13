# O0442 — Split an indirect call into a guarded direct call to its hottest target and the indirect fallback

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0442`.

## What it does

A call through a function value — a handler table, a callback, a comparison function — hides its
callee from every interprocedural pass: no inlining, no effect summary, no constant propagation into the
callee. When a profile shows that one function is the target most of the time, the call can be split: a
direct call under a test of the function value, the original indirect call otherwise. PB-Compiler does
this as indirect call promotion (PB O0271, implemented and registered before its inliner); the direct arm
is then an ordinary candidate for [#159](https://github.com/Hawkynt/Polypiler/issues/159) and `OPT-PGO-INLINE`. PRD §4.8.

## Example

### Before

```
site:
  %r = CallIndirect.i32 %handler(%x)              // targets: total 100, @common × 31, @rare × 30
```

### After

```
site:
  %handler.is = FuncRefEq.fn<(i32) -> i32> %handler, @common
  CondBr %handler.is, site.direct, site.indirect
site.direct:
  %r.direct = Call.i32 @common(%x)
  Br site.cont
site.indirect:
  %r.indirect = CallIndirect.i32 %handler(%x)
  Br site.cont
site.cont:
  %r = Phi.i32 [site.direct: %r.direct], [site.indirect: %r.indirect]
```

## When it applies

(1) The site has a `targets` entry; its first target's count is strictly greater than every other target's and `count × 100 ≥ 30 × total` in 128-bit integers. (2) The target is a function of the current module with a body or a declaration, and its return type, parameter count and parameter types equal the call site's. (3) `FuncRefEq %f, @g` is true iff `%f` refers to exactly the function `@g` and carries no captured environment; it is pure and cannot trap ([#114](https://github.com/Hawkynt/Polypiler/issues/114)). The target profile must lower it (a pointer comparison in C, Rust, Go and machine code; a delegate-target comparison in C#; reference identity in JavaScript and Python); a target without such a lowering makes the pass inactive for that profile. (4) The site's block is split after the call's argument computations; successors' phis that named the old block name the continuation. (5) The fallback is the original `CallIndirect` with the same callee value and arguments; its target data is cleared. (6) The site is not inside a protected region whose handler tables the split would have to duplicate — the three new blocks stay in the site's region. **Without a profile** (`--profile-use` absent, or the function stale or unmeasured) the pass does nothing and writes no report entry: every count it asks `IProfileData` for is null, and null never satisfies a threshold. PRD §4.8: no translation needs a profile. **Determinism** (PRD §13.1): the decision is integer arithmetic on counts read through `IProfileData`, candidates are visited in module and block order, ties are broken by block number or name — the same input, compiler, profile and seed give the same output, and the profile's digest is in the manifest's command line (`OPT-PGO-COLLECT`).

## How it is proven

`structural`: when `%f` is `@g`, `CallIndirect %f(args)` and `Call @g(args)` are the same call; otherwise the original call runs. The profile chooses `@g` and has no part in correctness. No solver, no timeout path.

## When it must not fire

- Promoting without the guard: every other handler is replaced by the hot one.
- Comparing only the code pointer of a closure: two closures over the same lambda with different captured variables compare equal and the direct call runs with the wrong environment.
- A profile from an older build naming a function whose signature changed: a direct call with the old argument list is ill-typed; rule 2 refuses it.
- Two targets with equal counts promoted by list order: the result would depend on how the profile happened to be sorted.
- Promoting a target at 30 % while another has 60 %: the guard fails most of the time; rule 1 requires the *hottest*.

## Switching it off

`--disable-opt O0442`; active at `--opt balanced` and `speed`; off at `size`, `memory` and `none` (the guard and the second call add code).

## Targets

every target that can compare function values by identity, source and machine code: the downstream compiler has no target histogram. In generated source it is an `if (f == common) common(x) else f(x)`, which reads naturally; the objective of [#155](https://github.com/Hawkynt/Polypiler/issues/155) keeps it off the size-oriented levels, and a profile whose capability set has no identity comparison of function values switches it off entirely.

## Interactions with other passes

module pass in phase `optimize` with `Order` before [#159](https://github.com/Hawkynt/Polypiler/issues/159) (upstream registers it before its inliner), so that the direct arm is priced and — with `OPT-PGO-INLINE` — inlined in the same phase. The three new blocks inherit the site's profile key; the direct arm's count is the target's count, the fallback's the rest. Changes the CFG; invalidates it and the call graph.
