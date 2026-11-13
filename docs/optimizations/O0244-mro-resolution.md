# O0244 — Resolve members and cooperative super calls through a linearized method resolution order

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#762](https://github.com/Hawkynt/Polypiler/issues/762). Once implemented it is switched off with `--disable-opt O0244`.

## What it does

Python resolves `obj.method` by walking the class's C3 linearization, and `super().method()` continues in
the linearization *of the object's class*, not in the base of the class the code is written in. PRD §30.1
row "C++ multiple/diamond inheritance and Python multiple inheritance" names "C3 as applicable"; §30.5
requires a Python MRO fixture; §32 lists "C3 MRO" among the dispatch tests; §33.1 O2 "MRO policies".

## Example

### Before

```
; class A: who() = 1;  class B(A): who() = 20 + super().who();  class C(A): who() = 300 + super().who()
; class D(B, C): who() = 4000 + super().who()            linearization of D: D, B, C, A
func @B.who(%self: ref<B>) -> i32 { entry: %n = Object.CallNext.i32 %self, B::who(); %r = Add.i32[wrap] 20, %n; Ret.i32 %r }
```

### After

```
func @B.who(%self: ref<B>) -> i32 {
entry: %t = Load.u16[bounds] (FieldAddr.ptr<u16> %self, __tag)
       Switch.u16 %t, bad, [2: asB], [4: asD]              ; tags: A 1, B 2, C 3, D 4
asB:   %n1 = Call.i32 @A.who(%self)                         ; next after B in (B, A)
       Br join
asD:   %n2 = Call.i32 @C.who(%self)                         ; next after B in (D, B, C, A)
       Br join
bad:   Trap.Unreachable
join:  %n = Phi.i32 [asB: %n1], [asD: %n2]
       %r = Add.i32[wrap] 20, %n
       Ret.i32 %r }
```

## When it applies

(1) The linearization L(C) is C followed by the C3 merge of the linearizations
of its bases and the list of its bases; when the merge finds no candidate the hierarchy is rejected. (2)
`dispatch=mro` binds, per concrete class, the first entry of L(class) declaring the member; `CallNext`
from shape S binds the first entry after S. (3) The `Switch` lists exactly the concrete classes whose
linearization contains S (for `CallNext`) or the static receiver class (for `dispatch=mro`). (4) A class
with no later declaration of the member in its linearization gets a case that traps with the label mapped
from Python's `AttributeError`. (5) The hierarchy is closed and static, as stated under *In scope*.

## How it is proven

`structural+interpreter`: the linearization is a pure function of the base lists;
validated against the Python interpreter's `__mro__` on generated hierarchies and by interpreting both
forms on every fixture.

## When it must not fire

- Binding `super().who()` in `B` to `A.who` statically: for a `D` object the next class after `B` is `C`;
  the result would be 4021 instead of 4321.
- Depth-first left-to-right resolution: for `D(B, C)` it visits `A` before `C`, so `C.who` is skipped.
- `class X(A, B)` with `B(A)`: no consistent linearization exists; Python raises `TypeError` at class
  creation — picking any order is wrong.
- Applying C3 to C++ classes: C++ reports ambiguity instead of choosing.

## Switching it off

`--disable-opt O0244`; `Kind = Required`: the rule runs at every `--opt` level, including
`none`, on targets whose capabilities lack `Feature(method-resolution-order)`. Disabled, the legalizer has
no alternative for these operations and reports `PLY0702`.

## Interactions with other passes

runs before [#768](https://github.com/Hawkynt/Polypiler/issues/768)/[#470](https://github.com/Hawkynt/Polypiler/issues/470) lower the resulting direct calls and tags; a
`Switch` with one case folds to a direct call.
