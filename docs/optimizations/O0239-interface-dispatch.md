# O0239 — Lower interface calls to tag dispatch trees or per-type dispatch tables

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#473](https://github.com/Hawkynt/Polypiler/issues/473). Once implemented it is switched off with `--disable-opt O0239`.

## What it does

An interface call selects its implementation from the dynamic type of the receiver. Targets without
interfaces need that selection spelled out. PRD §30.1 row "Classes, interfaces, abstract classes": method
tables and dispatch are the semantics, "virtual/interface tables" the fallback; §33.1 O1 asks for first
interfaces through explicit procedure dispatch; §32 lists interface identity and vtable calls among the
dispatch tests.

## Example

### Before

```
%a = Object.Call.i32[dispatch=interface, trap NullReference] %s, IArea::Area()
```

### After

```
       %n = PtrIsNull.bool %s
       Assert (Not.bool %n) [trap NullReference]
       %t = Load.u16[bounds] (FieldAddr.ptr<u16> %s, __tag)
       Switch.u16 %t, bad, [1: rect], [2: square]
rect:   %a1 = Call.i32 @Rect.Area(%s)
        Br join
square: %a2 = Call.i32 @Square.Area(%s)
        Br join
bad:    Trap.Unreachable
join:   %a = Phi.i32 [rect: %a1], [square: %a2]
```

## When it applies

(1) The case list contains exactly the concrete shapes S with `IsSubtype(S,
I)`, each bound to the function its shape declares for `I::Member` (the explicit implementation if there
is one, else the public member of that name and signature, searched from S towards its bases). (2)
`dispatch-tree` requires that no shape outside the module can implement I. (3) The null test precedes the
tag load. (4) Arguments are evaluated once, before the `Switch`. (5) In `dispatch-table`, the table of
shape S for interface I lists members in I's declaration order and the tag index is total over the
implementing shapes.

## How it is proven

`structural+interpreter`: the case list or table is computed from the shape table;
validated by interpreting the `Object.Call` form and the lowered form with every implementing shape as
receiver.

## When it must not fire

- Binding by member name only: `IA.Value()` and `IB.Value()` implemented explicitly by different functions
  would both call the first.
- Building the tree from the classes that happen to be instantiated in one function: a shape created
  elsewhere reaches `bad`.
- Evaluating an argument expression inside each case: an effectful argument would be evaluated after the
  tag load instead of before the call sequence.
- Treating the interface reference as a second object with its own identity: `ReferenceEquals(obj,
  (IArea)obj)` must stay true.

## Switching it off

`--disable-opt O0239`; `Kind = Required`: the rule runs at every `--opt` level, including
`none`, on targets whose capabilities lack `Feature(interface-dispatch)`. Disabled, the legalizer has no
alternative for these operations and reports `PLY0702`.

## Interactions with other passes

runs after [#470](https://github.com/Hawkynt/Polypiler/issues/470) (needs `__tag`) and after [#755](https://github.com/Hawkynt/Polypiler/issues/755) removed provable
calls; a tree with one case is a direct call.
