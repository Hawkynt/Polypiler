# O0240 — Lower single inheritance and virtual calls to prefix records and per-type dispatch

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#768](https://github.com/Hawkynt/Polypiler/issues/768). Once implemented it is switched off with `--disable-opt O0240`.

## What it does

A derived class is its base plus more fields, and a virtual call runs the most derived override. On C and
PowerBASIC both must be constructed: the base as a prefix of the derived record, the dispatch as a tag test
or a table. PRD §33.1 O2 "virtual/interface tables … generated class fallbacks for procedural targets"; §32
lists method hiding and vtable calls. C# and C++ disagree on what a virtual call inside a constructor
reaches — that difference is part of the contract.

## Example

### Before

```
shape Base    [kind=class, storage=reference] { ctor @Base.new; method Kind = @Base.Kind [dispatch=virtual] }
shape Derived [kind=class, storage=reference] { base Base; ctor @Derived.new; method Kind = @Derived.Kind [dispatch=override] }
%k = Object.Call.i32[dispatch=virtual, trap NullReference] %b, Base::Kind()
```

### After

```
type Base    = record { __tag: u16, tag: i32 }
type Derived = record { __base: Base }
       %t = Load.u16[bounds] (FieldAddr.ptr<u16> %b, __tag)        ; after the null test
       Switch.u16 %t, bad, [1: isBase], [2: isDerived]
isBase:    %k1 = Call.i32 @Base.Kind(%b)
           Br join
isDerived: %k2 = Call.i32 @Derived.Kind(%b)
           Br join
bad:       Trap.Unreachable
join:      %k = Phi.i32 [isBase: %k1], [isDerived: %k2]
```

## When it applies

(1) The base record is the first member of the derived record and the tag is
the first member of the root record, so every reference in a hierarchy addresses the tag at offset 0. (2)
The override map binds, for concrete shape S and member M, the implementation of the nearest shape on the
path from S to M's declaring shape that declares M with `override`, `virtual` or `final`. (3) Under
`final-type` the tag is stored once, before the root constructor; under `constructing-type` each
constructor level stores its own tag after its base constructor returned and before its own body. (4) A
hidden member is never entered into the override map of the member it hides. (5) The preconditions of
[#473](https://github.com/Hawkynt/Polypiler/issues/473) for argument evaluation and the null test apply.

## How it is proven

`structural+interpreter`: validated by interpreting `Object.Call` and the lowered form
for every concrete shape as receiver, under both `ctorDispatch` values.

## When it must not fire

- `class Base { public int tag; public Base() { tag = Kind(); } public virtual int Kind() => 1; }` and
  `Derived` overriding `Kind` to return 2: `new Derived().tag` is 2 in C# and 1 in the equivalent C++;
  using one policy for both is wrong for one of them.
- Entering a hiding member (`new int Kind()`) into the override map: a call through `Base` would run the
  derived method; C# runs the base one.
- Placing derived fields before the base record: a base reference would read derived data.
- Binding `base.Kind()` through dispatch: it recurses into the override.

## Switching it off

`--disable-opt O0240`; `Kind = Required`: the rule runs at every `--opt` level, including
`none`, on targets whose capabilities lack `Feature(virtual-dispatch)`. Disabled, the legalizer has no
alternative for these operations and reports `PLY0702`.

## Interactions with other passes

runs after [#470](https://github.com/Hawkynt/Polypiler/issues/470) and [#755](https://github.com/Hawkynt/Polypiler/issues/755); shares the alternatives and cost keys of
[#473](https://github.com/Hawkynt/Polypiler/issues/473).
