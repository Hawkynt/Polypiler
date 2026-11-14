# Value records

The first aggregate of the IR: a record of named fields that is copied by value, as a C# `struct` or
`record struct` and a JavaScript object literal of fixed shape are. The IR states the semantics once
(`IrStructNew` in `Polypiler.Core.IR`), the interpreter enforces them, and both backends reproduce
them in every target profile.
Related: [repository README](../../README.md) · [IR and analysis](../architecture/IR.md) ·
[integer and bit operations](integer-operations.md).

## Semantics

- **Declarations.** A module lists its declared types in `IrModule.Types`. An `IrStructType` has a
  name and `IrField`s (name, type, read-only flag) in declaration order. Types are nominal: two
  structs with the same fields are two types, and a field belongs to the one declaration that lists
  it. A field is a truth value, a number, a struct, or an array of numbers; a struct never contains
  itself.
- **Value semantics.** Every read of a struct value yields a copy: assignment, argument passing,
  return, a store into an array and a load from one each copy. A field store through a place changes
  that place alone; `a[i].X = 3` writes the element itself. An array field is held by reference, as
  every array is.
- **Default.** Every number zero, every truth value false, every struct field its own default. A
  struct with an array field has no default; it cannot be the element of `new T[n]`.
- **Equality.** `IrStructEquals` compares field by field in declaration order: integers and truth
  values by value, floating-point numbers as `double.Equals` does (NaN equals NaN, `+0.0` equals
  `-0.0`), a struct field recursively, an array field by identity. That is what a C# `record struct`
  does: `new P2(double.NaN, 0.0) == new P2(double.NaN, -0.0)` is `True`.
- **Evaluation order.** The arguments of `IrStructNew` are evaluated in field order. A field store
  evaluates its place (array, index, which is checked) first, then the value.

## Nodes and verifier rules

| Node | Meaning | Verifier |
| --- | --- | --- |
| `IrStructNew(Type, Arguments)` | a value from one argument per field, in declaration order | struct type of the module; one argument per field, of the field's type |
| `IrStructDefault(Type)` | the default value | struct type of the module that has a default |
| `IrFieldLoad(Target, Field)` | the value of a field | the field is one of the target type's own |
| `IrFieldStore(Target, Field, Value)` | a statement: a write through a place | the target is a place (variable, array element, field of a place); the field is not read-only; the value has its type |
| `IrStructEquals(Left, Right)` | field-wise equality | both operands of one struct type |

`IrNewArray` of a struct fills every element with the default; `ArrayOf` lists structs. `ArrayCopy`,
`ArrayFill` and `ArrayClear` stay on arrays of numbers. A module constant is never a struct.

**Extending the declarations.** `IrStructType` derives from `IrTypeDeclaration`, which owns the name,
the fields and their deferred definition (`Define`, so that declarations can refer to each other in
any order). A class with reference semantics is another subclass with the same fields and the same
`IrFieldLoad`/`IrFieldStore`, and its own `IrType` kind; a generic declaration adds its type
parameters to the declaration and its type arguments to `IrType`, which already compares a declared
type by the declaration it names.

## C# frontend

| Construct | IR | Status |
| --- | --- | --- |
| `struct` / `readonly struct` with fields, at the top of the file or nested in the static class | `IrStructType` | supported |
| `record struct R(T a, …)` positional, `readonly record struct` | `IrStructType` with `IsEquatable` | supported |
| constructor that gives every field a parameter or a constant; `new T()`, `new()`; `default(T)`, `default` | `IrStructNew`, `IrStructDefault` | supported |
| object initializer `new T { F = v }` | in a declaration, an assignment to a variable or a `return`: `IrStructDefault` then `IrFieldStore`s on the variable or a temporary; inside an expression: one `IrStructNew` | supported |
| field read / write on a local, a parameter, an array element, a nested field; compound assignment and `++` as a statement | `IrFieldLoad`, `IrFieldStore` | supported |
| `==`, `!=`, `Equals(T)` of a record struct | `IrStructEquals` | supported |
| `Equals(object)`, `GetHashCode`, `ToString`, `with`, `ref`/`in`/`out`, methods, properties, operators | — | `PLY1002` naming it |
| `==` on a plain struct, `ValueType.Equals` | — | `PLY1000` from Roslyn / `PLY1002` |
| a constructor with any other statement, field initializers, a constructor that calls another | — | `PLY1002` |
| `ref struct`, `partial`, generic structs, interfaces, attributes such as `StructLayout`, static members | — | `PLY1002` |

A constructor is inlined into the value it builds. Where it takes its parameters in another order
than the fields, or does not use one, the arguments must be free of effects; an object initializer
inside an expression likewise.

## JavaScript frontend

An object literal is a value record when the program cannot tell a copy from a reference: it is never
written after it is built, never given a new property, never compared by identity, and handed only to
functions of the file. Each refused construct is named, so nothing is imported that would compute
differently.

| Construct | IR | Status |
| --- | --- | --- |
| `{ x: e1, y: e2 }`, `{ x, y }` with number, Boolean or record values | `IrStructNew` | supported |
| `p.x`; `const { x, y } = p`, `const { x: other } = p` | `IrFieldLoad` | supported |
| a record as a parameter, a result, a `const`/`let` value, an element of an array literal | struct values | supported |
| `p.x = v`, `p.x++` after construction | — | `PLY1102`: an object literal written after it is built |
| `p.z = v` | — | `PLY1102`: a new property of an object literal |
| `delete p.x`, `p === q`, `p != q`, computed keys, spread, methods, getters, setters, string keys | — | `PLY1102` naming it |
| one variable reached by two shapes | — | `PLY1105` |

Each distinct shape - the keys in source order and the IR type of each value - is one struct, named
after the first variable or parameter in source order that holds it, in PascalCase (`point` becomes
`Point`); a shape held only in arrays is named after the first array, a nested one after its key, and
a name already taken gets a number (`P2`). Every field is read-only. A literal whose values have
narrower types than the place it goes to (`{ x: 1 }` where `{ x: number }` is held) is converted key
by key.

## C# backend

Struct declarations are written in front of the static class, in the namespace.

| IR | net2.0 (C# 2) · net3.5 (C# 3) · net4.6 (C# 6) | net4.8 · netstandard2.0 (C# 7.3) · netstandard2.1 · netcoreapp3.1 (C# 8) | net8.0 (C# 12) · net10.0 (C# 14) |
| --- | --- | --- | --- |
| struct whose fields are all read-only | `public struct` with `public readonly` fields and a constructor | `public readonly struct` | `public readonly struct` |
| other struct | `public struct` with public fields and a constructor | same | same |
| struct with equality (a source `record struct`, compared, or a field of one) | `public struct T : System.IEquatable<T>` with `Equals(T)`, `Equals(object)`, `GetHashCode()`, `==`, `!=`; fields compared with `EqualityComparer<F>.Default` | same | `public record struct T(…)`; a struct-typed component is a field |
| `IrStructNew` | `new T(a, b)` | same | same |
| `IrStructDefault` | `new T()` | `default` where the type is given, `default(T)` elsewhere | same |
| `IrStructEquals` | `a == b` (`a.Equals(a)` for one variable) | same | same |
| hash code | `unchecked` `h = h * -1521134295 + EqualityComparer<F>.Default.GetHashCode(f)` | same; `System.HashCode.Combine` (more than eight fields: `HashCode.Add`) from netstandard2.1 | the compiler's |

The rows are `CSharpCapabilities` rows: `ReadOnlyStruct` (C# 7.2), `RecordStruct` (C# 10),
`DefaultLiteral` (C# 7.1), `ObjectInitializer` (C# 3, recorded; constructors are written instead), and
the APIs `IEquatable<T>`, `EqualityComparer<T>.Default` (.NET Framework 2.0), `HashCode.Combine`
(.NET Standard 2.1) and `IsExternalInit` (.NET 5): a `readonly record struct` needs its init accessors,
so with `--langversion 10` on an older framework a read-only struct with equality is written out. No
helper is needed and no row is `PLY1053` in any profile.

## JavaScript backend

| IR | every level (es5 with and without `--typed-arrays`, es2015 to es2022) |
| --- | --- |
| `IrStructNew` | `{ x: a, y: b }`; a key whose value is the variable of its name is `{ x }` from ES2015 (capability `shorthand property`, replaced by `key: key`) |
| copy where the place it is read from stays live | `{ x: p.x, y: p.y }`; through `$copy_T(p)` where `T` has a struct field or the place is an array element |
| a fresh value, a call result, a variable that is dead afterwards | no copy |
| `IrStructDefault` | `{ x: 0, y: 0 }`: `false` for a truth value, `0n` or `{ hi: 0, lo: 0 }` for a 64-bit integer as the profile holds one |
| `IrNewArray` of structs | `$newArray_T(n)`: a plain `Array` of `n` distinct default literals |
| `IrStructEquals` | `$equals_T(a, b)`: per floating-point field `a.x === b.x \|\| (a.x !== a.x && b.x !== b.x)`, per 64-bit field of halves `$eq64`, per struct field `$equals_U`, otherwise `===` |
| field read / write | `p.x`, `p.x = v`; an element through the bounds-checked `$get(a, i).x` |

**Liveness.** A read of a variable is copied when the variable is read, written through or passed
again later in the function (in evaluation order, both branches of an `if` counted), or when the read
stands in a loop the variable is declared outside of. An array element is always copied. A public
function is called from outside: a struct parameter it writes through is copied on entry, and every
other read of a struct parameter as a value is copied, so a caller's object is never changed or
handed back. The analysis does not follow paths: a later read keeps a variable live even where an
assignment in between replaces it, which costs a copy and never a wrong result.
