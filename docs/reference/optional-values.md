# Optional values

Values that may be absent: C#'s `int?`, `??`, `?.` and `??=`, and JavaScript's `null` and
`undefined`. The IR has one optional type and one absent value with explicit operations; it states
no lifting rule, so C#'s lifted `null + 1` (`null`) and JavaScript's coerced `null + 1` (`1`) are both
written out by their frontend and neither direction changes a result. A reference - an object or an
array - is absent as `null`, which the IR had before.
Related: [repository README](../../README.md) · [classes](classes.md) · [value records](records.md) ·
[IR and analysis](../architecture/IR.md).

## The model

- **Type.** `IrType.Optional(T)`, written `T?`, holds a value of `T` or none. `T` is a truth value,
  a number or a struct; a string, an array or an object is never optional - an array or an object is
  `null` instead (`IrNull`, `IrIsNull`), and a string is never absent. An optional value is no field
  of a struct or a class and no element of an array or a [collection](collections.md) yet.
- **Operations.** `IrNone(T?)`, `IrSome(v)`, `IrIsNone(x)`, `IrUnwrap(x)` - which traps with
  `IrTrap.MissingValue` when `x` holds none - and `IrCoalesce(x, fallback)`, which evaluates `x` once
  and the fallback only when `x` holds none; the fallback is of `T` (the result is a `T`) or of `T?`.
  `IrUnwrap` carries `IsTested` where the frontend has tested the value first, the operand of a
  lifted operation: the meaning is the same, a backend may leave the test out.
- **Single evaluation.** `IrBind(local, value)` is an assignment as an expression: the frontend
  evaluates an operand it reads twice - the receiver of `?.`, an operand of a lifted operator - once
  into a temporary declared at the start of the function, and reads the temporary after it.
- **Boundary.** None crosses as `null` in both directions of the interpreter. An exported function
  carries `IrFunction.Absent`: how JavaScript spells its absent result, `Null` or `Undefined`.

| Node | Meaning | Verifier |
| --- | --- | --- |
| `IrNone(T)` | no value | `T` is optional |
| `IrSome(v)` | the value as an optional one | `v` is a truth value, a number or a struct |
| `IrIsNone(x)` | whether `x` holds none | `x` is optional |
| `IrUnwrap(x)` | the value, a `MissingValue` trap for none | `x` is optional |
| `IrCoalesce(x, f)` | `x`'s value, or `f` for none | `x` is optional, `f` of its type or of the type it holds |
| `IrIsNull(r)` | whether a reference is `null` | `r` is an object or an array |
| `IrNull(T)` | `null` | `T` is a class of the module or an array |
| `IrBind(l, v)` | `l = v` as a value | `l` in scope, not the receiver, `v` of its type |

## C# frontend

| Construct | IR | Status |
| --- | --- | --- |
| `T?`, `Nullable<T>` of the scalar types; `null`, `default` | `T?`, `IrNone` | supported |
| `HasValue`, `Value`, `GetValueOrDefault()`, `GetValueOrDefault(d)`, `(T)x`, `(T?)v`, `int?` to `long?` | `IrIsNone`, `IrUnwrap`, `IrCoalesce`, `IrSome`, a conversion where it holds a value | supported |
| `a ?? b`, `x ??= v` as a statement and as the value of a variable | `IrCoalesce`; an `if` on `IrIsNone`; `IrCoalesce` with `IrBind` | supported |
| `a?.Length`, `a?[i]`, `o?.f` on arrays and objects | `IrConditional(IrIsNull(IrBind(t, a)), none, member of t)` | supported |
| `+ - * / % & \| ^ << >> >>> - ~ !` on nullable values | none if an operand is none, else the operator on the values | supported |
| `== !=` on nullable values, `x == null`, `x is null`, `x is not null` | two nones are equal, none and a value are not | supported |
| `< > <= >=` on nullable values | false if an operand is none | supported |
| `string?`, `int[]?`, the null-forgiving `!` | the type itself; no operation | supported (carried, not checked) |
| `is int i` and other type patterns, `&`/`\|` of `bool?` (three-valued), `++`/`--` of a nullable value, `?.` of anything but a length, an element or a field | - | `PLY1002` naming it |

Both operands of a lifted operator are evaluated first, left before right, as ECMA-334 §12.4.8
says; the test for none follows. `double? n = NaN; n == n` is `false`, as .NET computes it.

## JavaScript frontend

| Construct | IR | Status |
| --- | --- | --- |
| `null`, `undefined`, `void 0` joined with a number, a truth value or a bounded `BigInt` | `T?` (a number `undefined` reaches is a double) | supported |
| the same joined with an array or an object | `null` | supported |
| `return;`, the end of a function that returns a value elsewhere, a missing argument | `undefined` | supported |
| `x == null`, `x != null` | `IrIsNone` / `IrIsNull` - both absent values | supported |
| `x === null`, `x === undefined` | `IrIsNone`; false where that absent value never reaches `x` | supported |
| `a ?? b`, `a?.length`, `o?.f`, `x ??= v` | `IrCoalesce`, the conditional of `IrIsNull`, an `if` | supported |
| `+`, `-`, `<`, a condition, an index … of a value that may be absent | ToNumber written out: `null` is 0, `undefined` a NaN, through `IrCoalesce` | supported |
| a place both absent values reach that `===` tells apart | - | `PLY1105` |
| arithmetic on a value both may be | - | `PLY1102` |
| `obj?.[key]`, `f?.()`, a string or an object literal that may be absent | - | `PLY1102` naming it |

A parameter the function compares with `null` or `undefined` is one a caller may pass them as. A
test proves a variable present in its branch, its `else`, and behind `&&` and `||`, unless the
branch assigns it. The absent result of an exported function is spelled `undefined` where nothing
but `undefined` reaches it (`if (n > 0) return n;`), `null` otherwise.

## C# backend

| IR | net2.0, net3.5 (C# 2, 3) | net4.6 (C# 6) | net4.8, netstandard2.0 (C# 7.3) | netstandard2.1 … net10.0 (C# 8+) |
| --- | --- | --- | --- | --- |
| `T?`, none, a value, `IsNone`, `Unwrap`, `??` | `int?`, `null` (`(int?)null` in a branch), `x == null`, `x.Value`, `??` | same | same | same |
| `?.` of a variable's length, element or field | `(a == null) ? (int?)null : (int?)a.Length` | `a?.Length` | same | same |
| `if (x == null) x = v;` | as it stands | same | same | `x ??= v;` |
| `IrBind` | `(t = v)` | same | same | same |
| `MissingValue` | `.Value` throws `InvalidOperationException` "Nullable object must have a value." | same | same | same |
| reference annotations | none | none | none | none (the IR carries none) |

Rows: `null-conditional operator` (C# 6), `null-coalescing assignment` (C# 8), `nullable reference
types` (C# 8, recorded only). A lifted operation is written as the IR spells it out, which every
profile compiles.

## JavaScript backend

| IR | es5 | es2015, es2017 | es2020 | es2022 |
| --- | --- | --- | --- | --- |
| none, a value | `null`, the value | same | same | same |
| `IsNone`, `IsNull` | `x == null` | same | same | same |
| `??` | `x != null ? x : y`, `($c = f()) != null ? $c : y` - never `\|\|` | same | `x ?? y` | same |
| `?.` coalesced at once | `(a == null) ? null : a.length` | same | `a?.length ?? y` | same |
| `if (x == null) x = v;` | as it stands | same | same | `x ??= v;` |
| lifted `a + 1` | `(a == null) ? null : ((a + 1) \| 0)` | same | same | same |
| lifted `a < 1` | `(a == null) ? false : (a < 1)` | same | same | same |
| `Unwrap` of none | `$unwrap(x)`, a `RangeError` "missing value" | same | same | same |
| an exported result spelled `undefined` | `void 0` for none, `$undef(x)` for a value that may be none | same | same | same |

Rows: `??` (2020, `!= null ?`), `?.` (2020, `== null ?`), `??=` (2021, `!= null ?`). The printer never
mixes `??` with `&&` or `||` without parentheses (ECMA-262 §13.13).

## Known differences

- C# has one absent value. A JavaScript module read through C# and back returns `null` where the
  original returned `undefined`: the C# frontend records `Null`, as the C# backend writes no spelling.
- `MissingValue` is a `RangeError` in JavaScript, as every other trap of the IR, until
  12-exceptions-and-traps gives the traps classes of their own.
- `a?.length` is a double in JavaScript's types: `undefined` reaches it, and ToNumber of `undefined`
  is a NaN.
- Nullable reference annotations are accepted and dropped; `#nullable enable` is written nowhere.
