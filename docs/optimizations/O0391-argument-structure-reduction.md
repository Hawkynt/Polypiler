# O0391 — Pass only the fields a callee reads instead of the whole record, at every call site

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0391`.

## What it does

A function that takes a rectangle and reads its width and height does not need the rectangle. Passing the
record by reference forces the caller to keep it in memory and the callee to load through a pointer the alias
analysis must respect; passing it by value copies every field. Passing the two fields instead removes the
indirection or the copy at every call, lets the values travel in registers, and frees the caller's record for
scalar replacement. It rewrites the callee's interface, so it needs every caller — and, for by-reference
records, a proof that the fields do not change between the call and the callee's reads. PRD §4.8
(redundant allocation removal, copy elision).

## Example

### Before

```
type Rect = record { x: i16, y: i16, w: i16, h: i16 }

func @Area(%r: ptr<Rect>) -> i32 {
  %pw = FieldAddr.ptr<i16> %r, w
  %w  = Load.i16 %pw
  %ph = FieldAddr.ptr<i16> %r, h
  %h  = Load.i16 %ph
  %a  = Mul.i32[wrap] (SExt.i32 %w), (SExt.i32 %h)
  Ret.i32 %a
}
// caller: %a = Call.i32 @Area(%rc)
```

### After

```
func @Area(%w: i16, %h: i16) -> i32 {
  %a = Mul.i32[wrap] (SExt.i32 %w), (SExt.i32 %h)
  Ret.i32 %a
}
// caller:
//   %w0 = Load.i16 (FieldAddr.ptr<i16> %rc, w)
//   %h0 = Load.i16 (FieldAddr.ptr<i16> %rc, h)
//   %a  = Call.i32 @Area(%w0, %h0)
```

## When it applies

Common: (1) the callee is owned (`ModuleOwnership` of `OPT-IPA-CONSTARG`), not variadic, and the
parameter is not a `receiver`; (2) at most three distinct fields are read, all of scalar type. By value: (3)
every use of the parameter is `ExtractField`; nothing else is required, a value cannot change. By reference:
(4) every use of the parameter is `FieldAddr` whose result is used only as the address of non-volatile loads
of the field's type; (5) at every call site the argument is proven non-null ([#91](https://github.com/Hawkynt/Polypiler/issues/91)) and derives
from a region that contains the whole record ([#93](https://github.com/Hawkynt/Polypiler/issues/93)), so loading at the call site cannot trap
where the callee would not have; (6) no write to the selected fields can happen during the call before or
between the callee's loads: the callee's own stores and the write sets of its callees
([#87](https://github.com/Hawkynt/Polypiler/issues/87)) do not include those fields — a store through another parameter counts unless
[#82](https://github.com/Hawkynt/Polypiler/issues/82) proves it distinct from the record at every call site, and an indirect call, an external call
without a contract or an unknown write blocks the rewrite; (7) the caller's loads are inserted directly before
the call, after all argument evaluation. [#155](https://github.com/Hawkynt/Polypiler/issues/155) scores the scalar form no higher than the original, the
original listed first.

## How it is proven

`structural+alias-facts+effect-facts`: the callee reads the same field values it would have loaded, because no write can intervene between the caller's load and the point of the callee's load; dereferenceability at the call site excludes a new trap. No solver, no timeout path.

## When it must not fire

- `@AreaOut(%r, %out)` stores 0 through `%out` and then loads `r.w`; a caller passes the address of `r.w` as `%out`. The source computes 0 × h = 0; with the width loaded before the call the result is 3 × 4 = 12.
- A callee that loads the field only when a flag is set, called with a null record and the flag clear: loading at the call site traps where the source did not.
- A callee that calls an unknown external function between its two loads: the callback may change the record.
- A volatile field: the number and time of reads are observable.
- Four fields read: four scalars instead of one pointer is not a reduction.

## Targets

every target: fewer memory accesses and a record that can stay out of memory in machine code; in
generated source an internal helper takes `(w, h)` instead of a pointer, which most target languages can pass
in registers. Only internal functions change, so the public interface of a generated package is untouched.

## Switching it off

`--disable-opt O0391`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

module pass in phase `optimize` after `OPT-IPA-DEADPARAM` and before [#159](https://github.com/Hawkynt/Polypiler/issues/159) considers the callee again; enables [#500](https://github.com/Hawkynt/Polypiler/issues/500) of the caller's record, `OPT-IPA-CONSTARG` on the new scalar parameters and `OPT-IPA-ICF`.
