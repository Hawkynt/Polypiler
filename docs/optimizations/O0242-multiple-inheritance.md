# O0242 — Lower non-virtual multiple inheritance to embedded base records with reference adjustment

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#761](https://github.com/Hawkynt/Polypiler/issues/761). Once implemented it is switched off with `--disable-opt O0242`.

## What it does

With two bases a derived object contains two base subobjects, and a reference to the second one does not
point at the start of the object. With a non-virtual diamond it even contains the common base twice. PRD
§31: "D derives from B and C and both contain distinct A base subobjects … the generated UDT and methods
preserve both A subobject identities; pointer adjustments and dispatched operations match the C++ oracle."
§30.1 names the base subobject graph and pointer adjustment as the semantics, explicit base records as the
fallback.

## Example

### Before

```
shape A [kind=class] { method id = @A.id [dispatch=virtual] }          ; field a: i32
shape B [kind=class] { base A }                                         ; field b: i32
shape C [kind=class] { base A; method id = @C.id [dispatch=override] }  ; field c: i32
shape D [kind=class] { base B; base C }                                 ; field d: i32
%ac = Object.Upcast.ref<A>[path=C] %d
%i  = Object.Call.i32[dispatch=virtual, trap NullReference] %ac, A::id()
```

### After

```
type A = record { __tag: u16, __top: u16, a: i32 }
type B = record { __base_A: A, b: i32 }
type C = record { __base_A: A, c: i32 }
type D = record { __base_B: B, __base_C: C, d: i32 }
%pc = FieldAddr.ptr<C> %d, __base_C
%ac = FieldAddr.ptr<A> %pc, __base_A
%t  = Load.u16[bounds] (FieldAddr.ptr<u16> %ac, __tag)
%o  = Load.u16[bounds] (FieldAddr.ptr<u16> %ac, __top)
; Switch on %t: case D -> the final overrider on the path through C is @C.id, called with %ac adjusted to the C subobject
```

## When it applies

(1) Every upcast to a base that occurs more than once carries a `path`;
without one the conversion is ambiguous and blocked. (2) The override map is computed per (complete shape,
subobject path): the final overrider of member M for the subobject reached through path P is the most
derived declaration on P and its extensions towards the complete shape; two candidates neither of which
overrides the other block the hierarchy. (3) `__top` of a subobject equals its offset in the layout
computed for the target profile and is stored before any virtual call can observe the object. (4) A callee
always receives a reference to the subobject of its own declaring shape. (5) A downcast subtracts exactly
the offset the matching upcast added and is legal only along one path.

## How it is proven

`structural+interpreter`: validated by interpreting the `Object.*` form and the lowered
form on every fixture, comparing results, field stores through each path, and all pairwise identity tests.

## When it must not fire

- Merging the two `A` subobjects of the non-virtual diamond into one: `d.B::a = 10; d.C::a = 20;` must
  read back 10 and 20; a merged `A` reads 20 twice.
- Passing the complete-object reference to `C::id` unadjusted: its field accesses read `B`'s fields.
- Comparing references to the two `A` subobjects after adjusting both to the object start: they would
  compare equal; C++ says they differ.
- Upcasting `D` to `A` without a path by taking the first occurrence: C++ rejects it as ambiguous.

## Switching it off

`--disable-opt O0242`; `Kind = Required`: the rule runs at every `--opt` level, including
`none`, on targets whose capabilities lack `Feature(multiple-inheritance)`. Disabled, the legalizer has no
alternative for these operations and reports `PLY0702`.

## Interactions with other passes

extends [#768](https://github.com/Hawkynt/Polypiler/issues/768) (single base is the special case with offset 0 and no `__top`);
runs before [#470](https://github.com/Hawkynt/Polypiler/issues/470) fixes the reference representation.
