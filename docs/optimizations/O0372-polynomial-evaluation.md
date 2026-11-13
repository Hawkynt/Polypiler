# O0372 — Evaluate integer polynomials in one variable in Horner or Estrin form

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0372`.

## What it does

`a*x^3 + b*x^2 + c*x + d` written out multiplies the powers separately: four multiplications for a cubic where
Horner's form `((a*x + b)*x + c)*x + d` needs three, or two when the leading coefficient is 1. Checksums,
hash mixers and fixed-point approximations contain such expressions, and [#158](https://github.com/Hawkynt/Polypiler/issues/158) refuses the re-association
that would expose the sharing. For wrapping integers the two forms are the same polynomial modulo `2^w`, so the
rewrite is exact there — and only there. PRD §4.8 (strength reduction), §4.1 (no unproven floating-point
reassociation).

## Example

### Before

```
%x2 = Mul.u32[wrap] %x, %x
%x3 = Mul.u32[wrap] %x2, %x
%t2 = Mul.u32[wrap] 3, %x2
%t1 = Mul.u32[wrap] 5, %x
%s1 = Add.u32[wrap] %x3, %t2
%s2 = Add.u32[wrap] %s1, %t1
%y  = Add.u32[wrap] %s2, 7                // x^3 + 3x^2 + 5x + 7: four multiplications
```

### After

```
%h1 = Add.u32[wrap] %x, 3
%h2 = Mul.u32[wrap] %h1, %x
%h3 = Add.u32[wrap] %h2, 5
%h4 = Mul.u32[wrap] %h3, %x
%y  = Add.u32[wrap] %h4, 7                // ((x + 3)x + 5)x + 7: two multiplications
```

## When it applies

(1) The root and every interior node are `Add`, `Sub`, `Neg` or `Mul` with the `[wrap]` contract on
one integer type of width `w`; the leaves are one SSA value `x` and literals. (2) The expansion has degree
between 2 and 8; the coefficients are reduced modulo `2^w`. (3) The removable set is the least fixed point of
"interior node whose every user is the root's replacement or another removable node"; nodes with a user
outside stay and are not counted as saved. (4) The chosen plan — Horner, or Estrin under the rule in scope —
uses strictly fewer multiplications than the removable set contains. (5) The plan expands to the same
coefficient vector. (6) [#155](https://github.com/Hawkynt/Polypiler/issues/155) scores the plan strictly below the original; the original is listed
first. A float type, or any other contract, refuses.

## How it is proven

`structural+exhaustive-8bit`: two expressions with the same coefficient vector are the same function on the ring of integers modulo `2^w`; the vectors are compared by the pass and re-derived independently in the test, and every fixture shape is checked over all 8-bit values. No solver, no timeout path.

## When it must not fire

- `x*x - 10*x` under `Mul.i8[trap Overflow]` at `x = 12`: the source traps on 144; the Horner form `(x - 10) * x` returns 24.
- The same expression in `f64` at `x = 0.1`, `x*x*x + 3*x*x`, re-associated: the two forms differ in the last bit for some inputs, and re-association is what PRD §4.1 forbids.
- A shared `x*x` with a user outside the polynomial: counting it as saved reports a win that does not exist — it stays live and the plan adds its own multiplications.
- `x*x + x`: Horner `(x + 1) * x` also needs one multiplication; rewriting it is churn that defeats later matching.
- Coefficients compared as mathematical integers instead of modulo `2^w`: `x * 0x80000000 * 2` at `u32` is the zero polynomial.

## Targets

every target: the count of multiplications drops in machine code and in generated source alike,
and Horner form is the idiom a programmer would write. A mature target compiler re-associates wrapping integer
arithmetic only partly and does not reliably reach Horner form, so the rewrite is not redundant there; the
cost model still gates it, and with equal multiplication counts nothing changes.

## Switching it off

`--disable-opt O0372`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` after [#158](https://github.com/Hawkynt/Polypiler/issues/158) (shared powers are already one node) and before [#165](https://github.com/Hawkynt/Polypiler/issues/165), which then reduces the remaining constant multiplications; requests a `recognize` sweep when it changed a function.
