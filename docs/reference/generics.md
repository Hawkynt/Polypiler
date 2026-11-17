# Generics

Generic methods and classes in the IR: there are none. A generic method or class of the source is
**monomorphized** - one function or class per list of type arguments the module reaches, each fully
typed - and each instantiation carries where it came from (`IrGenericOrigin`), so that a backend can
give the result an idiomatic shape again: one generic definition in C#, one function in JavaScript
where the instantiations are written the same. The verifier, the interpreter, the optimizer and both
backends only ever meet closed types.
Related: [repository README](../../README.md) · [classes](classes.md) · [value records](records.md) ·
[IR and analysis](../architecture/IR.md).

## The IR

- **Instantiations.** `IndexOf<T>` used with `double` and `int` becomes the functions `IndexOf<f64>`
  and `IndexOf<i32>`; `Ring<T>` used with `long` and `double` the classes `Ring<i64>` and
  `Ring<f64>`, with members `Ring<i64>.Push` and so on. The name is `IrGenerics.Mangle`: the
  definition's name and the type arguments as the IR writes them. `IrFunction.Origin` and
  `IrClassType.Origin` hold `IrGenericOrigin(Name, TypeParameters, TypeArguments, Constraints)`.
- **Templates.** The origin of a generic method also holds its `IrGenericTemplate`: the body lowered
  once more with each type parameter as an interface of its own name that no module declares, the
  calls it makes of instantiations in terms of its parameters, `default(T)` as `IrNull` of the
  parameter and `new T()` as `IrNew` of it. `IrGenerics.Instantiate` turns a template into an
  instantiation; `IrGenerics.Equivalent` compares two functions node by node. A template is never
  verified, interpreted or emitted as it stands.
- **Equality and order.** What a generic definition calls is not the operator: `IrGenericEquals` and
  `IrGenericCompare` state .NET's meaning on the instantiated type (table below), so that one node
  stands in every instantiation and the template.
- **Verifier.** An origin has one type argument per type parameter and names its instantiation
  (`Name<arguments>`); a constraint names a parameter of its origin; no type of the module is open -
  none is the stand-in of a template parameter; both operands of the equality and the order have one
  type the operation is defined on.

## Equality and order

| Type | `IEquatable<T>.Equals`, `EqualityComparer<T>.Default.Equals` | `IComparable<T>.CompareTo`, `Comparer<T>.Default.Compare` |
| --- | --- | --- |
| `sbyte`, `byte`, `short`, `ushort`, `char` | by value | the difference of the two values, as .NET's `CompareTo` returns it |
| `int`, `uint`, `long`, `ulong` | by value | -1, 0 or 1 |
| `bool` | by value | -1, 0 or 1, `false` first |
| `float`, `double` | NaN equals NaN, `-0.0` equals `0.0` (SameValueZero) | -1, 0 or 1; NaN below every number and equal to NaN; `-0.0` equal to `0.0` |
| a class | identity; a record by its fields | refused (`IComparable<C>` is a generic interface) |

.NET 10 gives `IndexOf([1.0, NaN], NaN)` 1 where `==` gives -1, and node's `[1, NaN].indexOf(NaN)`
is -1: neither `===` alone nor `indexOf`/`includes` is ever written for it.

## C# frontend

| Construct | IR | Status |
| --- | --- | --- |
| generic static method of the static class, closed by inference or explicit arguments | one `IrFunction` per instantiation the module reaches, `IndexOf<f64>` | supported |
| generic class beside the static class (no base class or interface), its fields, constructors, methods and properties | one `IrClassType` per instantiation, `Ring<i64>`, each with the template of the class (`IrGenericClassTemplate`: `Ring<T>` lowered with its parameters, one template per member function) where every member can be stated with the parameters | supported |
| constraints `struct`, `class`, `unmanaged`, `notnull`, `new()`, `IEquatable<T>`, `IComparable<T>`, an interface of the file | checked by the compiler, kept in the origin | supported |
| `default(T)`, `default` of a type parameter | the default of the argument: zero, `false`, the default struct, `null` | supported |
| a parameter of a function type (`Func<T, bool>`, `Action<T>`) | the function type of the instantiation ([functions and closures](functions-and-closures.md)) | supported |
| `new T()` under `new()` | `IrNew` of a class by its constructor without parameters, the default of a value type | supported |
| `a.Equals(b)` of `IEquatable<T>` or of a number with one of its type, `EqualityComparer<T>.Default.Equals` | `IrGenericEquals` | supported |
| `a.CompareTo(b)` of `IComparable<T>` or of a number with one of its type, `Comparer<T>.Default.Compare` | `IrGenericCompare` | supported |
| a public generic method that nothing in the module instantiates | - | `PLY1002`: "'IndexOf' has no instantiation; name one with --instantiate" |
| polymorphic recursion (`Depth<T>` calls `Depth<T[]>`), instantiations that do not end | - | `PLY1002`: "the instantiations of 'Depth<T>' do not end" |
| generic math (a constraint of `System.Numerics`), `typeof(T)`, `is T`, `as T`, variance, another constraint, a generic method of a class, a static field of a generic class, a generic class in a hierarchy | - | `PLY1002` naming it |

Instantiations are collected from the module's own code: from every method that is not generic, from
the bodies of the instantiations, and from `--instantiate`. A private generic method nothing calls is
dropped. `--instantiate "<Method>[<type>,…]"` (repeatable, or several separated by commas) names an
instantiation of a public generic method the class itself does not use; the types are C# types - a
keyword, a type of the file, an array of either. A malformed instantiation, a method that is no
generic method of the class, a type that is none and another number of type arguments are mistakes
of the command line: `PLY0003`, exit code 2.

## JavaScript frontend

A function that is not exported, is no member of a class and is not called from an initializer of the
module, and that its calls pass values of different kinds - an `Int32Array` here, a `Float64Array`
there - is **copied** once per combination of kinds; each copy is typed on its own and each call calls
the copy of its kind, so evaluation order and the number of calls do not change. A copy is named like
an instantiation, `middle<i32[]>`, without an origin. An exported function keeps one type per
parameter: a parameter that arrays of two kinds reach is `PLY1105` ("'a' holds both int32[] and float64[]").

## C# backend

| IR | every profile (`net2.0` … `net10.0`) | below C# 7.3 (`net2.0`, `net3.5`, `net4.6`) | below C# 8 (… `netstandard2.0`) |
| --- | --- | --- | --- |
| instantiations of one method that are its template instantiated | one generic definition with its constraints, `IndexOf<T>(T[] a, T v) where T : global::System.IEquatable<T>`; calls `IndexOf<double>(…)` | one that needs `unmanaged` is written per instantiation | one that needs `notnull` is written per instantiation |
| any other instantiation of a method | `IndexOf_Double(…)` | same | same |
| an instantiation of a class | `Ring_Int64`, `Ring_Double` | same | same |
| copies of a function (no origin) | overloads of one name: `middle(int[] a)`, `middle(double[] a)` | same | same |
| `IrGenericEquals` / `IrGenericCompare` | `a.Equals(b)` / `a.CompareTo(b)` of a number, a truth value and a parameter with the interface; `EqualityComparer<T>.Default.Equals(a, b)` / `Comparer<T>.Default.Compare(a, b)` otherwise | same | same |
| `IrNull` / `IrNew` of a parameter in a template | `default(T)` / `new T()` | same | same |

A set is written back as one definition only where every instantiation is its template instantiated
with its own arguments (`IrGenerics.Equivalent`), never where they differ in anything but the type,
and only where every instantiation its template calls is written back too. The rows `generic
constraint unmanaged` (C# 7.3) and `generic constraint notnull` (C# 8) of `CSharpCapabilities`
decide the profile columns; `IEquatable<T>`, `IComparable<T>`, `EqualityComparer<T>` and `Comparer<T>`
exist from .NET Framework 2.0. `PLY1053` is unreachable. A member never takes the name of its class:
the copies of `middle` in the module `middle` are `middle_`.

## JavaScript backend

| IR | es5 (with or without typed arrays) | es2015 · es2017 | es2020 · es2022 |
| --- | --- | --- | --- |
| instantiations or copies whose declarations are the same text once each bears the source name | one function or class under the source name (`Swap`, `Max`, `middle`); the comment above it lists every type a parameter takes | same | same |
| any other | `name$type` per instantiation: `IndexOf$f64`, `IndexOf$i32`, `Ring$i64`, `Ring$f64`, `middle$i32Array` | same | same; an `i64` instantiation uses `BigInt` and `BigInt64Array`, which makes its text differ from an `f64` one |
| `IrGenericEquals` of floating point | `a === b \|\| (a !== a && b !== b)` over variables and constants, the helper `$sameValueZero` where an operand is none or the module has two or more | same | same |
| `IrGenericEquals` of integers, truth values, references; of a record | `===` (the halves' comparison for a 64-bit integer at es5); `$equals_<Record>` | same | same |
| `IrGenericCompare` | the difference of two narrow integers; the comparisons of two pairs of halves; else the helper `$compare` | `$compare` | `$compare` |

The sets are decided together, because a declaration's text includes the names of what it calls: all
are taken to merge, a set whose texts differ is split, until no decision changes. The order of a
64-bit value that is no variable has no form below ECMAScript 2020 (`PLY1152`); nothing of this
issue's fixtures needs it.

## Known differences

- An instantiation of a generic class is a class per instantiation in C# and JavaScript; the Rust
  backend writes the template back as a generic struct ([the Rust backend](../architecture/RUST-BACKEND.md#generic-classes)).
  A class whose member converts a parameter to text has no template.
- A generic method of a class, a static field of a generic class and a generic class with a base class
  or an interface are refused; so are arrays of class references (`T[]` with a class `T`), which the
  IR does not have yet.
- Shared-code generics (one body for every reference type, as the CLR does), generic math and
  TypeScript's or JSDoc's `@template` generics are later work (#472, #219, #746).
