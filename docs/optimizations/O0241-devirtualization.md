# O0241 — Replace virtual and interface calls by direct calls when the receiver type is proven

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#755](https://github.com/Hawkynt/Polypiler/issues/755). Once implemented it is switched off with `--disable-opt O0241`.

## What it does

PRD §30.2 (OBJ-002): "Prefer elimination of unnecessary object machinery before emulation. Devirtualize
dispatch when receiver type is proved." A dispatch tree on a 6502 or a table lookup in a hot loop is pure
overhead when the object was created two lines earlier or its class is sealed. Removing the dispatch also
removes the reason for a type tag, which shrinks every object on targets where bytes matter.

## Example

### Before

```
%s = Object.New.ref<Square> @Square.new(5)
%u = Object.Upcast.ref<Shape2D> %s
%a = Object.Call.i32[dispatch=virtual, trap NullReference] %u, Shape2D::Area()
```

### After

```
%s = Object.New.ref<Square> @Square.new(5)
%u = Object.Upcast.ref<Shape2D> %s
%a = Call.i32 @Square.Area(%u)
```

## When it applies

(1) `exact`: `ExactType(receiver) = S` holds on every path to the call; the
callee is the override-map entry of S. (2) `final`/sealed: no shape below the binding shape can rebind the
member. (3) `unique`: the module is closed for the static receiver shape (neither it nor any descendant is
exported as extensible) and all concrete descendants bind the member to the same function. (4) The null
trap is kept unless the receiver is proven non-null (`Object.New` result). (5) Argument evaluation is
unchanged.

## How it is proven

`structural+interpreter` with the type facts of (1)–(3) recorded per rewrite (`exact`,
`final`, `unique`); a missing fact leaves the call unchanged. No solver.

## When it must not fire

- `unique` in an open module: an exported non-sealed `Shape2D` may be subclassed by the user of the
  generated library; binding to the only implementation present is wrong for their subclass.
- `exact` through a `Phi` of `Square` and `Rect`: the type is not exact; binding to either is wrong on the
  other path.
- Dropping the null test for a receiver loaded from a field: the direct call would run the callee on null.
- Treating the static type of the reference as its dynamic type: `Shape2D %u` holding a `Square` must run
  `Square.Area`.

## Switching it off

`--disable-opt O0241`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt
none`.

## Interactions with other passes

runs in phase `optimize` after constant propagation and before inlining; reduces the
work of [#768](https://github.com/Hawkynt/Polypiler/issues/768) and [#473](https://github.com/Hawkynt/Polypiler/issues/473) and can remove the tag requirement.
