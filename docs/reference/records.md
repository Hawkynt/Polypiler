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
| a literal whose shape reaches a place that objects of a class reach (#1388) | an object of a class made for the shape - see below | supported |

Each distinct shape - the keys in source order and the IR type of each value - is one struct, named
after the first variable or parameter in source order that holds it, in PascalCase (`point` becomes
`Point`); a shape held only in arrays is named after the first array, a nested one after its key, and
a name already taken gets a number (`P2`). Every field is read-only. A literal whose values have
narrower types than the place it goes to (`{ x: 1 }` where `{ x: number }` is held) is converted key
by key.

### Records where objects of a class are kept (#1388)

The Cipher framework keeps its test vectors as `TestCase` objects, and most algorithms write them as
literals (`this.tests = [{ text, uri, key, input, expected }]`) that `RegisterAlgorithm` turns into
`TestCase` objects, copying the keys the class lacks onto them. A place such literals and objects of a
class both reach is no record: the literals of that shape are objects of a class made for it.

| Construct | IR | Status |
| --- | --- | --- |
| a shape that meets objects of `C` at one place | a class `CLiteral` derived from the nearest class of the file every class of the place derives from; its own fields are the keys that class lacks, in the literal's order; the keys it has are its inherited fields, their types joined | supported |
| the literal | `IrNew` of a constructor that `SkipsBase`: it runs no constructor of the lineage, as a literal runs none, and stores the values in the literal's order, then `undefined` into each field the literal lacks | supported |
| `x instanceof C` where `x` may be such a literal | `IrTypeTest` of `C` and not of any class made for literals below it: false for a literal | supported for a variable |
| `Object.keys(o)` of a value that may be such a literal | the keys of the class `o` is of: the literal's in its order, otherwise the fields base first; a loop over them is written once per class | supported for a variable |
| `r[key] = o[key]` in a loop over `Object.keys(o)`, of keys `C` lacks, where `r` is a `const` of one `new C(…)` | the `new` makes an object of a class `CExtended` derived from `C`, with the keys copied as fields - one per list of keys, chosen by the class of `o`: a genuine `C` | supported where `o` is a `const` or a parameter never assigned and every key is copied on every pass |
| one shape whose key holds values of two kinds | — | `PLY1105` |
| a literal meeting a class of the library (`Error`) or of a frozen object | — | `PLY1105` |
| `instanceof` or `Object.keys` of an expression that may be such a literal and is no variable; keys copied from a variable assigned again, or only under a condition | — | `PLY1102` naming it |

### JSDoc typedef records (#1409)

A `@typedef {Object} Name` with `@property {T} key` tags, as the Cipher framework's
`MerkleDamgardOptions`, is a type JSDoc can name wherever it names a class: `@param {Name} options`.
It is a class of read-only fields, one per `@property` in source order, not a struct: an optional key,
`[key]` or `[key=default]`, may be missing, and a struct has no field that may be absent. An optional
key is its stated type or `undefined` (`int?` in C#, `null` in the JavaScript written back); the
default JSDoc writes down documents what the function does, it is not stored. An integer stays one: an
operator that reads it unchecked sees a NaN where it is undefined.

| Construct | IR | Status |
| --- | --- | --- |
| `f({ blockSize: 64, lengthBytes: 8 })` where `@param {Name}` | `IrNew` of the typedef's class, none for each optional key left out | supported |
| `options \|\| {}` of an object that is never absent | `options`: an object counts as true, `{}` is never evaluated | supported |
| `options \|\| {}` of `@param {Name\|null}` | the object, or one of every key `undefined` | supported |
| `s.key === undefined ? d : s.key`, `!==`, `==`, `!=`, `null` | the branch reads the value present (`IrUnwrap`) | supported |
| `s.flag === true`, `s.flag !== false` | `undefined` equals no truth value | supported |
| a literal that leaves out a required key, or has a key the typedef lacks | — | `PLY1105` naming the key |
| keys in another order than the `@property` tags with values that have an effect | — | `PLY1102` |
| `s.key = v` | — | `PLY1102`: a write into the field of the typedef |
| `@typedef {Array}`, unions, a property of no supported type or of the typedef itself | — | `PLY1101`: no supported JSDoc type |

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
