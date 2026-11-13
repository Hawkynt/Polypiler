# O0231 — Monomorphize closed generic types and functions

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#472](https://github.com/Hawkynt/Polypiler/issues/472). Once implemented it is switched off with `--disable-opt O0231`.

## What it does

C, PowerBASIC, Perl and the BASIC dialects have no type parameters. PRD §30.1 row "Generic types/methods"
names monomorphization as the first fallback and §30.2 (OBJ-002) requires it "when closed instantiations are
known"; §3.1 lists "templates after monomorphization" for C++. Without this rule a `Matrix<double>` or
`Max<T>` cannot reach any target that lacks generics, and the numerical-class demonstration of §33.2 item 4
is blocked.

## Example

### Before

```
func @Max<T>(%a: T, %b: T) -> T where T: IComparable<T> {
entry: %c = Object.Call.i32[dispatch=interface] %a, IComparable<T>::CompareTo(%b)
       %g = ICmp.sgt.i32 %c, 0
       %r = Select.T %g, %a, %b
       Ret.T %r }
%m = Call.i32 @Max<i32>(3, 7)
```

### After

```
func @Max_i32(%a: i32, %b: i32) -> i32 {
entry: %c = Call.i32 @i32.CompareTo(%a, %b)
       %g = ICmp.sgt.i32 %c, 0
       %r = Select.i32 %g, %a, %b
       Ret.i32 %r }
%m = Call.i32 @Max_i32(3, 7)
```

## When it applies

(1) Every type argument of the instantiation is closed (contains no type
parameter) after substitution along the reachability walk. (2) The argument satisfies each constraint
(`IsSubtype`). (3) The clone is verified by [#130](https://github.com/Hawkynt/Polypiler/issues/130) before it replaces a reference; a clone that
fails is a compiler error, never emitted. (4) A constraint call is devirtualized only for a sealed or
value-storage argument; otherwise it stays an `Object.Call` for [#473](https://github.com/Hawkynt/Polypiler/issues/473). (5) The number of
distinct instantiations of one generic does not exceed `maxInstantiations`. (6) Each instantiation of a
shape with statics gets its own storage.

## How it is proven

`structural+interpreter`: cloning with a total substitution of type parameters; every
clone re-verified; validated by interpreting the open module and the monomorphized module on every
fixture. No solver.

## When it must not fire

- Sharing one clone for `Counter<i32>` and `Counter<i64>` because their bodies are textually equal: the
  static `Count` would be shared, and the oracle prints 2 and 1, not 3 and 3.
- Devirtualizing a constraint call for a non-sealed class argument: a derived instance passed as `T` would
  run the base implementation.
- Polymorphic recursion `F<T>` calling `F<Pair<T>>`: the instantiation set is infinite; without the budget
  the compiler does not terminate.
- Substituting `T` by `i32` but leaving `Select.T`: the verifier rejects the clone — partial substitution
  is the bug this precondition catches.

## Switching it off

`--disable-opt O0231`; `Kind = Required`: the rule runs at every `--opt` level, including
`none`, on targets whose capabilities lack `Feature(generic-type)`. Disabled, the legalizer has no
alternative for these operations and reports `PLY0702`.

## Interactions with other passes

runs after [#475](https://github.com/Hawkynt/Polypiler/issues/475) (generic bodies contain resolved calls); its clones feed
[#470](https://github.com/Hawkynt/Polypiler/issues/470), [#473](https://github.com/Hawkynt/Polypiler/issues/473) and inlining; removes every open declaration that is not exported.
