# O0232 — Bind overloaded operators and user-defined conversions to named functions

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#474](https://github.com/Hawkynt/Polypiler/issues/474). Once implemented it is switched off with `--disable-opt O0232`.

## What it does

`a + b` on two vectors is a function call in disguise. C, PowerBASIC, Java and JavaScript cannot declare
operators on user types, so the call must be written out — with the operand order, the single evaluation of
each operand and the exact operator the source selected. PRD §30.1 groups operators and conversions with
overloads: resolution set, ranking, implicit conversions and evaluation order are the semantic information
to keep.

## Example

### Before

```
func @Vec2i.op_Add(%a: Vec2i, %b: Vec2i) -> Vec2i operator "+" { … }
func @Vec2i.op_Mul(%a: Vec2i, %k: i32) -> Vec2i operator "*" { … }
%s = Op.User.Vec2i[op="+", ranking=csharp] %p, %q
%t = Op.User.Vec2i[op="*", ranking=csharp] %s, 3
```

### After

```
%s = Call.Vec2i @Vec2i.op_Add(%p, %q)        ; exported as Vec2i_Add
%t = Call.Vec2i @Vec2i.op_Mul(%s, 3)         ; exported as Vec2i_Mul
```

## When it applies

(1) The candidate set is every function with `operator "<symbol>"` declared in
the shape of either operand (or of the single operand). (2) Exactly one candidate is best under
[#475](https://github.com/Hawkynt/Polypiler/issues/475)'s rule (3), counting a user-defined implicit conversion as worse than every built-in
implicit conversion. (3) Operands are passed in source order; no operand is swapped, duplicated or
dropped. (4) For `x op= y`, the address or index of `x` is computed once and reused for the load and the
store.

## How it is proven

`structural+interpreter`: a resolved call replaces an unresolved one; validated by
interpreting both forms on every fixture.

## When it must not fire

- `3 * v` when only `Vec2i * int` is declared: swapping the operands to make it fit computes the right
  number here and the wrong one for a non-commutative operator such as `-`; the C# compiler reports
  CS0019.
- Rewriting `a != b` as `Not(Call op_Eq)` when the type declares its own `!=` that logs: the log entry is
  lost.
- `arr[Next()] += v` lowered as `arr[Next()] = arr[Next()] + v`: `Next()` runs twice.
- Chaining two user-defined conversions (`A → B → C`) to make a call applicable: C# permits one.

## Switching it off

`--disable-opt O0232`; `Kind = Required`: the rule runs at every `--opt` level, including
`none`, on targets whose capabilities lack `Feature(operator-overload)`. Disabled, the legalizer has no
alternative for these operations and reports `PLY0702`.

## Interactions with other passes

uses the ranking of [#475](https://github.com/Hawkynt/Polypiler/issues/475); runs before [#472](https://github.com/Hawkynt/Polypiler/issues/472) clones bodies; the
resulting calls are candidates for inlining.
