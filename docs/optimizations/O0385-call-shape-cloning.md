# O0385 — Clone a function for the literal argument shape that a strict majority of its call sites share

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0385`.

## What it does

`OPT-IPA-CONSTARG` needs *every* call site to pass the same constant. One call with another value — a debug
path, a rarely used mode — and the function stays fully general for all of them. When most call sites agree,
a specialized copy for that shape gives them the folded body while the original serves the rest. Cloning costs
code, so it is bounded: one clone per function, a small body, and only where the saved call operands pay for
the copy. PRD §4.6 (specialization budget), §4.8.

## Example

### Before

```
func @Draw(%mode: i32, %x: i32) -> void { ...6 instructions testing %mode... }
func @main() -> void export "main" {
  Call.void @Draw(1, 10)   Call.void @Draw(1, 20)   Call.void @Draw(1, 30)
  Call.void @Draw(1, 40)   Call.void @Draw(1, 50)   Call.void @Draw(1, 60)
  Call.void @Draw(2, 70)
  Ret.void
}
```

### After

```
func @Draw(%mode: i32, %x: i32) -> void { ...unchanged... }
func @Draw$k1(%x: i32) -> void { ...the body with %mode = 1... }
func @main() -> void export "main" {
  Call.void @Draw$k1(10)   Call.void @Draw$k1(20)   Call.void @Draw$k1(30)
  Call.void @Draw$k1(40)   Call.void @Draw$k1(50)   Call.void @Draw$k1(60)
  Call.void @Draw(2, 70)
  Ret.void
}
```

## When it applies

(1) The callee is owned (`ModuleOwnership`), not variadic, has no clone yet and has at most 24
instructions. (2) Candidate shape: among the literal shapes of its call sites, the one matched by the most
call sites; ties are broken by the larger shape, then by the first call site in module order. (3) The number
`k` of matching call sites satisfies `2k > n` for `n` call sites and `k < n`. (4) Size rule: `k × |shape|`
(operands removed) is at least the callee's instruction count. (5) The clone is `@<name>$k1`, internal, with
the shape's parameters replaced by their literals (compared and inserted by bits) and removed from the
signature; its recursive self-calls that match the shape call the clone, others the original. (6) Exception
regions, attributes and source locations are copied; the clone records its origin for the source map. (7)
[#155](https://github.com/Hawkynt/Polypiler/issues/155) scores the cloned module no higher than the original under the active objective, the original
listed first — at `size` the size rule already guarantees it for the call operands alone.

## How it is proven

`structural`: a call site rebound to the clone passed exactly the literals the clone has substituted, so clone and original compute the same for it; other call sites are untouched. No solver, no timeout path.

## When it must not fire

- Rebinding a call site that passes the literal 1 for `%mode` but through a different parameter position: the shape is positional.
- Rebinding a call whose argument is a variable that *currently* holds 1: only literals match.
- A recursive call inside the clone that passes `%mode − 1`: it must target the original, not the clone.
- Three of seven call sites: no strict majority; a clone would serve a minority and double the code.
- A 200-instruction callee with two matching calls: the copy costs far more than two operands save.

## Targets

every target that benefits from the folded body; the clone is a real function, so code size grows by
at most 24 instructions per original function, and the size rule keeps the trade neutral in the count the
pass can see. For source targets the clone appears as a second, simpler function with a derived name; it is
created only for internal functions, so no public name changes.

## Switching it off

`--disable-opt O0385`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`; bounded by the limits in scope.

## Interactions with other passes

module pass in phase `optimize` after `OPT-IPA-CONSTARG` (which takes the unanimous cases) and before the per-function [#164](https://github.com/Hawkynt/Polypiler/issues/164) of the next iteration, which folds the clone; `OPT-IPA-DEADFUNC` removes the original when the remaining call sites disappear.
