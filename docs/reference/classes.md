# Classes

Reference objects in the IR: a class whose objects have an identity and mutable fields, made by its
constructors, with instance and static methods, properties and static fields - as a C# `sealed
class` or `record`, a JavaScript `class` and a constructor function of ECMAScript 5 are. The IR
states the semantics once (`IrNew` in `Polypiler.Core.IR/Objects`), the interpreter enforces them,
and both backends reproduce them in every target profile. Inheritance, interfaces and virtual
dispatch follow in #1320.
Related: [repository README](../../README.md) · [value records](records.md) ·
[module state](module-state.md) · [IR and analysis](../architecture/IR.md).

## The object model

- **Declarations.** An `IrClassType` is a declaration of `IrModule.Types` beside the structs: its
  fields (`IrField`s with a visibility), and its members, which `DefineMembers` gives it once -
  `IrConstructor`s, `IrMethod`s, `IrProperty`s and its static fields. `Base`, `Interfaces`,
  `IsSealed` and `IsAbstract` name what inheritance adds; every class is sealed so far.
- **Bodies.** The body of a constructor or a method is a function of the module (`IrModule.Functions`),
  which the member names; every pass that walks the functions of a module walks the members too. The
  function of a constructor or an instance method takes the receiver, `this`, as its first
  parameter, which the body never assigns; a static method has none. Such a function is reached
  through its class only: an `IrCall` of it is allowed only as the first statement of another
  constructor of the class, which is how one constructor chains to another (`this(…)`).
- **Properties.** An auto-property is a field of the class, its `BackingField`; it is read and
  written as that field. A computed property is a pair of accessor methods (`IrMethodKind.Getter`,
  `Setter`).
- **Static fields** are globals of the module ([module state](module-state.md)) named `Class.field`
  that the class owns; the backends write them as members of their class.
- **Identity.** An object is never copied: an assignment, an argument, a result and a field copy
  the reference, so a write through one copy is seen through every other. `IrReferenceEquals`
  compares identities; `null` (`IrNull`) is a reference that names no object.
- **Construction.** `IrNew` evaluates its arguments, makes the object with every field at its
  default - zero, false, the default struct, `null`, and no array - and runs the constructor on it.
  A constructor that does not chain first runs the field initializers of the class, which the
  frontend has written at its start, in declaration order.
- **Null.** Reading or writing a field of `null`, calling an instance method on it and reading an
  element or the length of an array field that holds no array trap with `IrTrap.NullReference`. A
  field read and a method call trap once the target is evaluated, before any argument; a field write
  and a setter - an assignment in both languages - evaluate the value first. That is what node does
  (`o.m(f())` throws before `f` runs, `o.f = f()` after it).
- **Records.** A class with `IsRecord` compares by value as well: `IrStructEquals` on it is null-safe
  (two `null`s are equal, `null` and an object are not) and compares the fields, a record field by
  the same equality; `IrReferenceEquals` stays identity. `new Range(2, 9) == new Range(2, 9)` is
  `True`, `ReferenceEquals(new Range(2, 9), new Range(2, 9))` is `False` - .NET 10.

## Nodes and verifier rules

| Node | Meaning | Verifier |
| --- | --- | --- |
| `IrNew(Type, Constructor, Arguments)` | a new object | class of the module; a constructor of that class; one argument per parameter after the receiver |
| `IrNull(Type)` | the reference to no object | a reference type of the module |
| `IrMethodCall(Type, Method, Target, Arguments)` | a call of an instance method on `Target`, or of a static method without one | a method of a class of the module; a target exactly for an instance method, of the method's class; arguments and result as the function declares |
| `IrReferenceEquals(Left, Right)` | identity | two references of one class |
| `IrFieldLoad` / `IrFieldStore` on an object | a field of the object | the field is one of the class's own; a read-only field is written only by a constructor of its class through the receiver |
| `IrStructEquals` on a record class | null-safe value equality | the class is a record |

The members themselves: every constructor and method names a function of the module that no other
member names; an instance member's function takes the receiver of its class first; a constructor
returns nothing; a getter takes nothing and returns a value, a setter takes one value and returns
nothing; an auto-property's field is the class's own and of its type; a static field is a global of
the module; a constructor that does not chain gives every read-only array field an array; no global
holds an object.

## C# frontend

| Construct | IR | Status |
| --- | --- | --- |
| `sealed class`, a class nothing in the file derives from, `record` / `record class` | `IrClassType` (sealed) | supported |
| instance, static and `readonly` fields, field initializers; `const` | fields, owned globals; a constant is folded into every use | supported |
| constructors, overloads, `this(…)` | `IrConstructor`s; the chained call first | supported |
| instance and static methods, overloads | `IrMethod`s (overloads share the name) | supported |
| auto-properties (`get`/`set`/`init`, initializers), computed properties (blocks, `=>`) | `IrProperty` with a backing field, or accessor methods | supported |
| `this`, `null`, `==`/`!=`/`is null`/`is not null`/`ReferenceEquals` on references | `IrLocalRef` of the receiver, `IrNull`, `IrReferenceEquals` | supported |
| `==`, `!=`, `Equals(T)` of a record | `IrStructEquals` | supported |
| `new C(…) { P = v }` in a declaration, an assignment or a `return` | `IrNew`, then the stores into the object | supported |
| inheritance, an interface, a generic class or method, an event, an indexer, an operator, a finalizer, a static constructor, a static property, an accessor with its own accessibility, `virtual`/`abstract`, `with`, a string field, an object initializer inside an expression | - | `PLY1002` naming it |

## JavaScript frontend

| Construct | IR | Status |
| --- | --- | --- |
| `class C { constructor(){…} m(){…} get p(){…} set p(v){…} static s(){…} }` | `IrClassType` | supported |
| `f = e;`, `#f`, `#m()`, `static f = e;` | fields (a private one `_f`), private members, owned globals | supported |
| `function C(a) { this.a = a; }` with `C.prototype.m = function …`, `Object.defineProperty(C.prototype, "p", { get, set })`, `C.s = function …`, `C.k = value` at the top level | `IrClassType` | supported |
| `new C(…)`, `this.f`, `this.m(…)`, `C.s(…)`, `o.p = v` through a setter | `IrNew`, field and method nodes | supported |
| `null`; `===`/`!==` between objects or with `null` | `IrNull`, `IrReferenceEquals` | supported |
| `extends`, a computed member name, a static block, prototype mutation, a property added outside the constructor, `delete`, a method taken from its object, `call`/`apply`/`bind` of a method, `this` outside a method, the class used as a value, a field with a value in a class without a constructor | - | `PLY1102` naming it |
| a field that values of two kinds reach | - | `PLY1105` |

The fields of a class are those it declares and those its constructor assigns through `this`, in the
order the source first names them. The type of a field is the join of every value stored into it
anywhere in the file: a double unless the program proves an integer, as for a variable; `null` joins
with a reference into that reference. A parameter of a method of an exported class is called from
outside and is a double unless JSDoc states otherwise.

## C# backend

| IR | net2.0 (C# 2) | net3.5 (C# 3) | net4.6 (C# 6) | net4.8 · netstandard2.0 (C# 7.3) · netstandard2.1 · netcoreapp3.1 (C# 8) | net8.0 (C# 12) · net10.0 (C# 14) |
| --- | --- | --- | --- | --- | --- |
| class | `public sealed class C` | same | same | same | same |
| field, static field | `private int _count;`, `public static int Created = 0;` | same | same | same | same |
| auto-property | a property over a private field `_P` | `{ get; set; }` | same | same | same; `{ get; init; }` where only constructors write it |
| get-only auto-property | a property over a `readonly` field | `{ get; private set; }` | `{ get; }` | same | same |
| computed property | accessor blocks | same | a getter alone as `=> …` | same | same |
| constructor | constructor; `: this(…)` for a chained one | same | same | same | same |
| record class | `sealed class C : IEquatable<C>` with `Equals(C)`, `Equals(object)`, `GetHashCode()` and null-safe `==`/`!=` | same | same | same | `public sealed record C(…)` where one constructor gives every init-only field its parameter |
| `IrNull`, `IrReferenceEquals` | `null`; `x == null` for a class without operators, `Object.ReferenceEquals` otherwise | same | same | same | same |
| `IrTrap.NullReference` | the runtime's own `NullReferenceException` | same | same | same | same |

Every row is a capability row (`auto-implemented property`, `get-only auto-property`, `init accessor`,
`record class`, the existing `expression-bodied member`); the init accessor and the record need the
type `IsExternalInit`, which the framework has from .NET 5. A class reaches the constants, globals,
functions and helpers of the static class as its members, which are then `internal`. No row needs a
helper of its own; `PLY1053` is unreachable.

## JavaScript backend

| IR | es5 (with and without `--typed-arrays`) | es2015 · es2017 · es2020 | es2022 |
| --- | --- | --- | --- |
| class | `function C(…) { … }` with methods on `C.prototype` | `class C { constructor(…) { … } m() { … } }` | same |
| instance field | `this.f = default;` first in the constructor, then the initializers | same | `f = default;` class field |
| private member | property `_name` (not enforced; the header says so) | same | `#name` |
| computed property | `Object.defineProperty(C.prototype, "p", { get: …, set: …, configurable: true })` | `get p() {…}` / `set p(v) {…}` | same |
| auto-property | a field of the property's name | same | same |
| static method | `C.s = function (…) {…};` | `static s(…) {…}` | same |
| static field | `C.created = 0;` after the constructor function | assignment after the class | `static created = 0;` |
| several constructors | a constructor that chooses `$init<k>` by `arguments.length` | same | same |
| record equality | `$equals_<Name>(a, b)`, null-safe | same | same |
| `IrNull`, `IrReferenceEquals` | `null`, `===` | same | same |
| `IrTrap.NullReference` | the engine's own `TypeError` | same | same |
| public class | `exports.C = C` (cjs), the global's property (iife) | `export class C` (esm) | same |

The rows are capability rows: `class` (2015, replacement `function`), `class accessor` (2015,
`Object.defineProperty`), `class fields` (2022, constructor assignments), `#private` (2022, `_name`),
`static fields` (2022, assignment after the class), `Object` and `Object.defineProperty` (5).
Constructors of one arity cannot be told apart by JavaScript and are `PLY1152`; nothing else is.

**Decision (privacy below ES2022).** A private member is an ordinary property with a leading
underscore at es5…es2020. The translated program cannot observe the difference; a consumer of the
exported class can, which the header of the file states
(`// Private members are '_'-prefixed properties at this level.`). A `WeakMap`-based emulation is not
used: it changes the cost of every field access.

## Known differences

- .NET evaluates the arguments of a call on `null` before it throws (`callvirt`); the IR and
  JavaScript trap first. The difference is visible only through state a later call of the same
  instance reads, since nothing in the IR catches a trap yet.
- A static field of a class keeps no visibility of its own; both backends write it public.
- A JavaScript field declared without a value and never assigned holds the IR's default (0, false,
  `null`) where JavaScript holds `undefined`.
- Mutable object literals of JavaScript (written after construction) stay refused; they wait for a
  class synthesized from their shape.
