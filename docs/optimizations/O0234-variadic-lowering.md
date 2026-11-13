# O0234 — Lower variadic parameters to arrays, fixed-arity clones or promoted argument slots

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#485](https://github.com/Hawkynt/Polypiler/issues/485). Once implemented it is switched off with `--disable-opt O0234`.

## What it does

C# `params`, Java varargs, JavaScript rest parameters and the C ellipsis all pass "the rest of the
arguments", and each does it differently: the first three build an array, C applies default argument
promotions and reads raw slots. PRD §30.1 row "C ellipsis, C# `params`, …": each language's collection,
conversions and ABI are the semantics; the fallbacks are fixed-arity specialization, arrays or a native
bridge. §32 forbids substituting one for another without a verified adapter; §30.5 names a C variadic
fixture.

## Example

### Before

```
func @Sum(%xs: darray<i32>) -> i32 variadic=array(i32) { … }
%r = Call.Variadic.i32 @Sum(; 1, 2, 3)
```

### After

```
%a = ArrayNew.darray<i32> 3
Store.i32[bounds] (ElemAddr.ptr<i32>[bounds] %a, 0), 1
Store.i32[bounds] (ElemAddr.ptr<i32>[bounds] %a, 1), 2
Store.i32[bounds] (ElemAddr.ptr<i32>[bounds] %a, 2), 3
%r = Call.i32 @Sum(%a)
; alternative fixed-arity: %r = Call.i32 @Sum_3(1, 2, 3)
```

## When it applies

(1) Rest arguments are evaluated left to right after the fixed ones, each
exactly once, before the array is filled. (2) The normal form applies only when there is exactly one rest
argument and its static type is `darray<T>`; otherwise an array is built even for one element. (3)
`fixed-arity` requires the escape fact "the array parameter is neither stored, returned nor passed on" and
that the callee reads its length only as a constant per clone. (4) `c-ellipsis`: every rest argument is
promoted before it is stored; `VaList.Next.<T>` is legal only for `i32`, `u32`, `i64`, `u64`, `f64` and
pointers. (5) Slots are read in order; reading more slots than were passed is the `[undefined]` contract.

## How it is proven

`structural+interpreter` with the escape fact of (3) from the alias analysis; validated
by interpreting both forms on every fixture. A missing escape fact selects the array alternative.

## When it must not fire

- Passing null for zero rest arguments: `Sum()` must see an array of length 0; a callee reading
  `xs.Length` would trap.
- Wrapping an array argument into a one-element array of arrays: `Sum(arr)` must pass `arr` itself — the
  callee's write to `xs[0]` is visible in `arr`.
- Reading a promoted `char` slot as 1 byte on a big-endian target: the value is in the low-order byte of
  an `i32`, not at the slot's first byte.
- Passing `f32` unpromoted: `va_arg(ap, double)` reads 8 bytes.

## Switching it off

`--disable-opt O0234`; `Kind = Required`: the rule runs at every `--opt` level, including
`none`, on targets whose capabilities lack `Feature(variadic-parameters)`. Disabled, the legalizer has no
alternative for these operations and reports `PLY0702`.

## Interactions with other passes

runs after [#475](https://github.com/Hawkynt/Polypiler/issues/475) and [#476](https://github.com/Hawkynt/Polypiler/issues/476); the array alternative feeds allocation
removal when the callee is inlined.
