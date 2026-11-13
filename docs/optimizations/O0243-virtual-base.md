# O0243 — Lower virtual base classes to one shared subobject located through the complete type

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#756](https://github.com/Hawkynt/Polypiler/issues/756). Once implemented it is switched off with `--disable-opt O0243`.

## What it does

With `virtual` inheritance the diamond has one shared base subobject, constructed once by the most derived
class, and its position depends on the complete type. PRD §31: "D virtually inherits one shared A through B
and C … both base paths refer to the same A subobject and construction and dispatch behavior match the
reference." §30.5 demands a real C++ diamond with both virtual and non-virtual inheritance; [#761](https://github.com/Hawkynt/Polypiler/issues/761)
covers the non-virtual half.

## Example

### Before

```
shape A [kind=class] { ctor @A.new; method id = @A.id [dispatch=virtual] }          ; field a: i32
shape B [kind=class] { base A [virtual]; ctor @B.new }
shape C [kind=class] { base A [virtual]; ctor @C.new; method id = @C.id [dispatch=override] }
shape D [kind=class] { base B; base C; ctor @D.new }
%ab = Object.Upcast.ref<A> (Object.Upcast.ref<B> %d)
%ac = Object.Upcast.ref<A> (Object.Upcast.ref<C> %d)
```

### After

```
type B_part = record { __tag: u16, __top: u16 }          ; B without its virtual base
type C_part = record { __tag: u16, __top: u16 }
type D = record { __base_B: B_part, __base_C: C_part, __vbase_A: A }
; both conversions yield FieldAddr.ptr<A> %d, __vbase_A, so %ab and %ac are the same reference
; @D.new: Call @A.new(vbase, 30); Call @B.new$base(…); Call @C.new$base(…); body of D
```

## When it applies

(1) A base inherited virtually on every path is one subobject; conversions
along any path yield the same reference. (2) The offset of a virtual base from a subobject is taken from
the complete type identified by the tag, never from the static type of the reference. (3) Only the
complete-object constructor of the most derived shape calls virtual-base constructors, before anything
else and with its own arguments; base-object constructors skip them. (4) Each virtual member has exactly
one final overrider in the complete shape. (5) The header of every subobject is stored before the first
virtual call can occur, following the module's `ctorDispatch`.

## How it is proven

`structural+interpreter`: validated by interpreting both forms on every fixture,
including the construction log and all pairwise identity tests.

## When it must not fire

- Letting `B`'s constructor initialize `A` with 10 and `C`'s with 20 inside `D`: C++ uses `D`'s
  initializer (30) and runs `A`'s constructor once; the naive version runs it twice and ends with 20.
- Computing the virtual base offset from the static type `B`: in a complete `B` object and in a `D` object
  the offset differs.
- Accepting both `B::id` and `C::id` as overriders without one in `D`: C++ rejects the class; choosing the
  first changes behaviour silently.
- Constructing bases in declaration order with the virtual base where it is first named: the virtual base
  must be first.

## Switching it off

`--disable-opt O0243`; `Kind = Required`: the rule runs at every `--opt` level, including
`none`, on targets whose capabilities lack `Feature(virtual-base)`. Disabled, the legalizer has no
alternative for these operations and reports `PLY0702`.

## Interactions with other passes

extends the subobject graph and constructors of [#761](https://github.com/Hawkynt/Polypiler/issues/761); dispatch uses
[#768](https://github.com/Hawkynt/Polypiler/issues/768)'s alternatives.
