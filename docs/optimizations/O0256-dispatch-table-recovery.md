# O0256 — Recover interface calls from constant function-pointer dispatch tables

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#953](https://github.com/Hawkynt/Polypiler/issues/953). Once implemented it is switched off with `--disable-opt O0256`.

## What it does

C code does polymorphism with a struct of function pointers and an `ops` field in each object. The C
frontend diagnoses "struct with a function-pointer member" and points here. PRD §33.1 O4 names dispatch
tables explicitly; §30.2 warns that identity and `is`/`as` outcomes must not be invented. Recovered as an
interface with implementing classes, such code becomes real C#, Kotlin or Rust trait objects instead of
delegates stored in records.

## Example

### Before

```
type ShapeOps = record { area: fn<(ptr<Shape>) -> i32> }
type Shape = record { ops: ptr<ShapeOps>, w: i32, h: i32 }
const @rect_ops: ShapeOps = { area: FuncRef @rect_area }
const @square_ops: ShapeOps = { area: FuncRef @square_area }
%t = Load.ptr<ShapeOps> (FieldAddr %s, ops)
%f = Load.fn<(ptr<Shape>) -> i32> (FieldAddr %t, area)
%a = CallIndirect.i32 %f(%s)
```

### After

```
shape IShapeOps [kind=interface] { method area: (IShapeOps) -> i32 [dispatch=abstract] }
shape Shape$rect   [kind=class, sealed] { implements IShapeOps; method area = @rect_area }
shape Shape$square [kind=class, sealed] { implements IShapeOps; method area = @square_area }
%a = Object.Call.i32[dispatch=interface, trap NullReference] %s, IShapeOps::area()
```

## When it applies

obligations (a)–(e) under *In scope*; plus: (f) every allocation site of the
object record stores its table in the same function or in a constructor-like function called immediately
on the fresh object; (g) a null check of v in the source, if present, corresponds to the null trap of the
recovered call — a call site that tolerates a null table by skipping the call blocks the recovery; (h) the
null-pointer behaviour of the original (`[undefined]` contract of a C dereference) may be strengthened to
a trap, never the reverse.

## How it is proven

`validated-rule+differential` with the listed fact obligations (reported as
`structural+interpreter` plus the fact classes): each candidate is re-lowered with O0239
(`dispatch-table`) and the result is compared with the original fragment on [#119](https://github.com/Hawkynt/Polypiler/issues/119) over the rule's
input generator; any difference, any `Unknown` fact or a budget expiry leaves the IR unchanged.

## When it must not fire

- `s->ops = &frozen_ops;` executed later to change behaviour: the object's "class" would change;
  obligation (b) fails.
- `a->ops->area(b)`: the receiver is not the object whose table was loaded; binding it as a method of `a`
  changes which data the callee reads.
- A table with a slot set to NULL and a caller that tests it: an optional operation is not an abstract
  member.
- Comparing `s->ops == &rect_ops` as a type test: after recovery the field is gone; the comparison must be
  recovered as `Object.Is` or block the rule — this rule blocks.

## Switching it off

`--disable-opt O0256`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt
none`; never required — with it disabled the low-level form is translated as it is.

## Interactions with other passes

runs in phase `recover` after constant propagation of table addresses; enables
devirtualization and native interface emission.
