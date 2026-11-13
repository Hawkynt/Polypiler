# O0246 — Lower type-switch and pattern-matching expressions to ordered decision trees

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#477](https://github.com/Hawkynt/Polypiler/issues/477). Once implemented it is switched off with `--disable-opt O0246`.

## What it does

`shape switch { Square q => …, Rect r when r.W > 10 => …, null => …, _ => … }` tests types, binds members,
checks a guard and falls through to the next arm, in a fixed order. PRD §30.1 row "Type-switch and
pattern-switch expressions": runtime type identity, deconstruction, guards, null, exhaustiveness and
evaluation order are the semantics; the fallback is decision trees with type and tag tests. The frontends
print the contract name `pattern-matching` for this; §31 requires it for the C# → C scenario.

## Example

### Before

```
%r = Match.i32[noMatch=trap InvalidOperation] %s {
  case Square %q                                            => { Mul.i32[wrap] (Object.Field.i32 %q, side), (Object.Field.i32 %q, side) }
  case Rect %t when λ(): ICmp.sgt.i32 (Object.Field.i32 %t, w), 10 => { Object.Field.i32 %t, w }
  case Rect %t                                              => { Add.i32[wrap] (Object.Field.i32 %t, w), (Object.Field.i32 %t, h) }
  case null                                                 => { 0 }
  case _                                                    => { -1 } }
```

### After

```
entry: %isNull = PtrIsNull.bool %s
       Br %isNull, armNull, t1
t1:    %isSq = Object.Is.bool %s, Square
       Br %isSq, armSquare, t2
t2:    %isRect = Object.Is.bool %s, Rect
       Br %isRect, guard, armDefault
guard: %t = Object.As.ref<Rect>[unchecked] %s
       %g = ICmp.sgt.i32 (Object.Field.i32 %t, w), 10
       Br %g, armWide, armRect
; armSquare, armWide, armRect, armNull, armDefault compute the arm values and branch to join
join:  %r = Phi.i32 [armSquare: %v1], [armWide: %v2], [armRect: %v3], [armNull: %v4], [armDefault: %v5]
```

## When it applies

(1) The order of tests respects the arm order: an arm's region is reached only
if every earlier arm's pattern or guard failed for this subject. A type pattern never matches null, so
testing null first is equivalent when no earlier arm can match null. (2) The subject expression is
evaluated exactly once. (3) Guards are evaluated at most once each and only on the path where their
pattern matched. (4) Bindings are introduced by `[unchecked]` casts and field reads dominated by the
successful test. (5) When no arm matches, the `noMatch` trap is raised; `unreachable` is used only with
the exhaustiveness proof of the verifier.

## How it is proven

`structural+interpreter`: validated by interpreting `Match` and the lowered tree on every
fixture, with one subject per arm, one per failed guard and one matching nothing.

## When it must not fire

- Testing `Rect` before `Square` because "both are rectangles": a `Square` would take the `Rect` arm.
- Evaluating the guard of arm 2 before the type test of arm 1: a guard with an effect runs for subjects
  arm 1 would have taken.
- Re-evaluating the subject expression per arm: `Next() switch { … }` would call `Next()` several times.
- Returning a default value when no arm matches: C# throws `SwitchExpressionException`.

## Switching it off

`--disable-opt O0246`; `Kind = Required`: the rule runs at every `--opt` level, including
`none`, on targets whose capabilities lack `Feature(pattern-matching)`. Disabled, the legalizer has no
alternative for these operations and reports `PLY0702`.

## Interactions with other passes

runs before [#470](https://github.com/Hawkynt/Polypiler/issues/470)' tag-test lowering and [#471](https://github.com/Hawkynt/Polypiler/issues/471); repeated `Object.Is`
tests on one subject are merged by value numbering afterwards.
