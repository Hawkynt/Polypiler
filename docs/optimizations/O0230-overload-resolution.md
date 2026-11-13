# O0230 — Resolve overloaded calls to direct calls with explicit conversions and mangled names

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#475](https://github.com/Hawkynt/Polypiler/issues/475). Once implemented it is switched off with `--disable-opt O0230`.

## What it does

C, PowerBASIC, Perl and JavaScript have one function per name. A source with `Add(int, int)`, `Add(long,
long)` and `Add(double, double)` therefore needs the choice made at compile time, the implicit conversions
written out, and distinct names. PRD §30.1 row "Overloaded functions": resolution set and ranking, implicit
conversions and evaluation order must be preserved; the fallback is early binding to mangled functions.
§30.2: overloads are resolved before target source emission.

## Example

### Before

```
func @Calc.Add$1(%a: i32, %b: i32) -> i32 overload "Calc.Add" { … }
func @Calc.Add$2(%a: i64, %b: i64) -> i64 overload "Calc.Add" { … }
func @Calc.Add$3(%a: f64, %b: f64) -> f64 overload "Calc.Add" { … }
%r = Call.Overload.i64[ranking=csharp] "Calc.Add"(%x, %y)        ; %x: i32, %y: i64
```

### After

```
%x1 = SignExtend.i64 %x
%r  = Call.i64 @Calc.Add$2(%x1, %y)
; on a target without overloading the functions are exported as
;   Calc.Add_i32_i32, Calc.Add_i64_i64, Calc.Add_f64_f64
```

## When it applies

(1) A candidate is applicable when it has as many parameters as the call has
arguments and each argument type is the parameter type or converts to it implicitly: integer to a wider
integer of the same signedness, unsigned to a strictly wider signed integer, any integer to `f32` or `f64`
(as C# and Java define it, even where precision is lost), `f32` to `f64`, `ref<D>` to `ref<B>` when
`IsSubtype(D, B)`. (2) Conversion to type T1 is better than to T2 when T1 is the argument's own type, or
T1 converts implicitly to T2 and T2 does not convert to T1. (3) Candidate A wins when for every argument
its conversion is not worse than B's and for at least one it is better, against every other applicable
candidate B. (4) Otherwise the call is not rewritten and is reported. (5) Conversions are inserted
immediately before the call in argument order; no argument is evaluated twice.

## How it is proven

`structural+interpreter`: the rewrite replaces a call by the call the ranking selects;
validated by running the unresolved form (the interpreter applies the same published rule through an
independent table-driven evaluator in the test project) and the resolved form on every fixture. No solver,
no timeout path.

## When it must not fire

- `Add(i32, i64)`: picking the first applicable candidate in declaration order would be right here by
  accident, but with the declarations reordered (`f64` first) it selects `Add(f64, f64)` and returns a
  rounded result for `9007199254740993`.
- `F(i32, i64)` and `F(i64, i32)` called with `(i32, i32)`: each is better on one argument; choosing
  either is wrong — the C# compiler reports CS0121.
- `u32` argument with candidates `(i32)` and `(i64)`: `u32` does not convert implicitly to `i32`; only
  `(i64)` is applicable.
- `u64` argument with candidates `(i64)` and `(f64)`: no implicit conversion from `u64` to `i64` exists,
  so `(f64)` is the only applicable candidate; choosing `(i64)` because "both are 64 bits" changes the
  value of `18446744073709551615` to `-1`.

## Switching it off

`--disable-opt O0230`; `Kind = Required`: the rule runs at every `--opt` level, including
`none`, on targets whose capabilities lack `Feature(overload-resolution)`. Disabled, the legalizer has no
alternative for these operations and reports `PLY0702`.

## Interactions with other passes

runs before [#472](https://github.com/Hawkynt/Polypiler/issues/472)' clones are named and before any emitter sees a
`Call.Overload`; enables inlining of the now direct calls.
