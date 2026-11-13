# O0237 — Lower classes with fields, methods and constructors to records, procedures and type tags

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#470](https://github.com/Hawkynt/Polypiler/issues/470). Once implemented it is switched off with `--disable-opt O0237`.

## What it does

A class is a record with an identity, a constructor and functions that receive it. C and PowerBASIC 3.5 have
records and procedures but no objects. PRD §30.1 row "Classes, interfaces, abstract classes" names object
identity, fields, constructors and visibility as the semantics and "UDT/struct + methods" as the fallback;
§33.1 O1 asks for first classes via UDT and explicit procedures. Every later rule of this area — interfaces,
virtual calls, closures — builds on the representation chosen here.

## Example

### Before

```
type Counter = record { value: i32 }
shape Counter [kind=class, storage=reference, equality=reference, sealed] { ctor @Counter.new; method Bump = @Counter.Bump }
%c = Object.New.ref<Counter> @Counter.new(10)
Call.void @Counter.Bump(%c, 5)
%v = Object.Field.i32[trap NullReference] %c, value
```

### After

```
%c = Alloc.ptr<Counter>[stack] 1                       ; alternative pointer, lifetime stack; zero-filled
Call.void @Counter.new(%c, 10)
Call.void @Counter.Bump(%c, 5)
%n = PtrIsNull.bool %c
Assert (Not.bool %n) [trap NullReference]
%v = Load.i32[bounds] (FieldAddr.ptr<i32> %c, value)
```

## When it applies

(1) Every reference to the shape in the module is rewritten with the same
alternative. (2) `stack` requires the fact that no reference to the object is stored to memory that
outlives the function, returned, or passed to a callee that retains it; `arena` requires the same with
respect to the exported entry; a missing fact selects `static`. (3) Storage is zero before the constructor
runs. (4) A null test precedes every field access and receiver call that carried `[trap NullReference]`,
unless a dominating test or the allocation itself proves the reference non-null. (5) `Object.Same` becomes
equality of the pointer or the handle, never of the fields. (6) In `pool-handle`, allocation beyond
`objectPoolSize` traps `OutOfMemory`; a released slot is reused only after its lifetime ended.

## How it is proven

`structural+alias-facts`: the rewrite substitutes a concrete representation for
references; lifetimes rest on escape facts; validated by interpreting both forms on every fixture with the
effect traces compared.

## When it must not fire

- Placing an object on the stack that is stored in a static field: the field dangles after the function
  returns.
- Comparing two `Counter(1)` objects by their fields for `ReferenceEquals`: distinct objects with equal
  fields would be "the same".
- Skipping the zero fill: a constructor that sets only some fields leaves the others undefined; C#
  guarantees 0.
- Reusing a pool slot while a handle to the previous object is still live: two logical objects alias.

## Switching it off

`--disable-opt O0237`; `Kind = Required`: the rule runs at every `--opt` level, including
`none`, on targets whose capabilities lack `Feature(class)`. Disabled, the legalizer has no alternative
for these operations and reports `PLY0702`.

## Interactions with other passes

runs after [#472](https://github.com/Hawkynt/Polypiler/issues/472) and [#478](https://github.com/Hawkynt/Polypiler/issues/478); provides the tag field and reference
representation that [#473](https://github.com/Hawkynt/Polypiler/issues/473), [#768](https://github.com/Hawkynt/Polypiler/issues/768) and [#477](https://github.com/Hawkynt/Polypiler/issues/477) use; `stack` objects become
candidates for allocation removal.
