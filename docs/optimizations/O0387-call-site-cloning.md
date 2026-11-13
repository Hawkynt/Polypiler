# O0387 — Clone a function for the call sites whose argument ranges or aliasing decide checks the joined facts leave open

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0387`.

## What it does

[#87](https://github.com/Hawkynt/Polypiler/issues/87) joins a parameter's range over all call sites. One call with an unknown divisor and the
callee keeps its division-by-zero check for the two calls that pass 1…16; one call with possibly overlapping
buffers and the callee's loop cannot hoist a load for the calls with distinct ones. Call sites that disagree
produce a weak common denominator. A copy of the function bound only to the call sites with the sharp facts
keeps those facts: its summary is joined over fewer sites, and its checks fold. PRD §4.4 (interprocedural
facts), §4.6 (specialization budget).

## Example

### Before

```
func @avg(%sum: u32, %count: u32) -> u32 {
  %q = UDiv.u32[trap DivideByZero] %sum, %count
  ...
}
// call sites: @avg(%s, %n) with %n in [1, 16];  @avg(%t, %m) with %m in [1, 100];  @avg(%u, %k) with %k unknown
```

### After

```
func @avg(%sum: u32, %count: u32) -> u32 { ...unchanged, serves the third call site... }
func @avg$r1(%sum: u32, %count: u32) -> u32 {    // summary: %count in [1, 100]
  %q = UDiv.u32[trap DivideByZero] %sum, %count // the trap is now disproved; O0008 relaxes it
  ...
}
// the first two call sites name @avg$r1
```

## When it applies

(1) The callee is owned (`ModuleOwnership`), not variadic, has at most 64 instructions and fewer than
2 clones made by this pass. (2) Range shape: a decision point `d` and a non-empty proper subset `A` of the call
sites such that the facts joined over `A` decide `d` and the facts joined over all sites do not; `A` is the
set of *all* sites whose own fact decides `d` the same way. (3) Aliasing shape: `A` is the set of sites at
which [#82](https://github.com/Hawkynt/Polypiler/issues/82) answers `NoAlias` for the two arguments; the callee has the store/load loop in scope; `A`
is a non-empty proper subset. (4) The trial clone is internal, named `@<name>$r<n>`; the sites of `A` are
rebound to it; for an aliasing shape the two parameters get `exclusive` with evidence `proven`, which is
sound because every bound site is proven. (5) After re-running the analyses on the clone at least one decision
point is decided there (for the aliasing shape: [#508](https://github.com/Hawkynt/Polypiler/issues/508)'s legality holds for at least one load). (6)
[#155](https://github.com/Hawkynt/Polypiler/issues/155) scores the cloned module strictly below the original under the active objective; otherwise the
clone is removed and the sites are bound back. (7) Candidates are tried in descending order of
`decided points × |A|`, ties by module order. Recursive calls inside the clone target the original.

## How it is proven

`structural+range-facts+alias-facts`: the clone is the callee's body; the facts used inside it are the analyses' own results for a function whose call sites are exactly `A`. The attribute `exclusive` is attached only where every bound site carries the alias proof. No solver, no timeout path.

## When it must not fire

- Binding a site with an unknown divisor to the clone: its summary range is no longer `[1, 100]`, and if the check was already removed the division by zero goes untrapped. Sites are bound before the clone is analysed, never after it was simplified.
- Marking parameters `exclusive` for a site where the buffers are merely *usually* distinct.
- A recursive call inside the clone that passes `%count − 1`: it can pass 0; it must target the original.
- Cloning for a fact that decides nothing in the callee: twice the code, no check removed.
- Three shapes for one function with a limit of two clones: the third is refused, not squeezed in.

## Targets

every target on which the decided checks cost something — explicit trap tests, bounds tests, loads
that cannot be hoisted. The clone doubles a function, so the cost model decides and the size and memory
levels stay out; clones are internal functions with derived names, so no public interface of a generated
package changes.

## Switching it off

`--disable-opt O0387`; active at `--opt balanced` and `speed`; off at `size`, `memory` and `none`.

## Interactions with other passes

module pass in phase `optimize` after `OPT-IPA-CONSTARG`, `OPT-IPA-CLONECONST` and [#159](https://github.com/Hawkynt/Polypiler/issues/159) (a callee small enough to inline is not worth cloning); the clones are simplified by [#163](https://github.com/Hawkynt/Polypiler/issues/163), [#502](https://github.com/Hawkynt/Polypiler/issues/502) and [#508](https://github.com/Hawkynt/Polypiler/issues/508) in the next iteration.
