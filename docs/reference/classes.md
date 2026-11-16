# Classes

Reference objects in the IR: a class whose objects have an identity and mutable fields, made by its
constructors, with instance and static methods, properties and static fields - as a C# `sealed
class` or `record`, a JavaScript `class` and a constructor function of ECMAScript 5 are. The IR
states the semantics once (`IrNew` in `Polypiler.Core.IR/Objects`), the interpreter enforces them,
and both backends reproduce them in every target profile, with single inheritance, abstract classes,
interfaces, virtual dispatch, base calls and type tests (see
[inheritance, interfaces and dispatch](#inheritance-interfaces-and-dispatch)).
Related: [repository README](../../README.md) · [value records](records.md) ·
[module state](module-state.md) · [IR and analysis](../architecture/IR.md).

## The object model

- **Declarations.** An `IrClassType` is a declaration of `IrModule.Types` beside the structs: its
  fields (`IrField`s with a visibility), and its members, which `DefineMembers` gives it once -
  `IrConstructor`s, `IrMethod`s, `IrProperty`s and its static fields. `Inherit` gives it its `Base`
  and its `Interfaces`; `IsSealed` and `IsAbstract` say what the source says.
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
| `IrMethodCall` with `Kind` `Virtual`, `Interface`, `Base` | the nearest override of the receiver's class; the implementation of an interface method; the method named, on `this` | see [dispatch](#inheritance-interfaces-and-dispatch) |
| `IrTypeTest(Value, Target)` | whether the reference names an object of the class or interface; false for `null` | a reference; a class or interface of the module |
| `IrCast(Type, Value)` | the reference itself for `null` or an object of the type; else the trap `InvalidCast` | the same |
| `IrAsCast(Type, Value)` | the reference for an object of the type, else `null`; never traps | the same |

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
| `class B : A`, `abstract class`, `abstract`/`virtual`/`override`/`sealed override` methods | `Base`, `IrMethod.Dispatch` | supported |
| `interface I { … }` with methods and properties, `class C : A, I`, implicit and explicit implementation, interface inheritance | `IrInterfaceType`, `Interface` calls | supported |
| `base.M(…)`, `: base(…)`, `protected`, `private protected` and `protected internal` (as protected) | `Base` calls, the base constructor first | supported |
| `x is T`, `x is not T`, `if (x is T t)` with `t` used in the branch, `x as T`, `(T)x`, upcasts | `IrTypeTest`, `IrAsCast`, `IrCast`; an upcast is the value | supported |
| a virtual or interface call on `this` reachable from a constructor | - | `PLY1002`: "a virtual call on 'this' during construction" |
| `new` or silent member hiding, a default interface method, a static abstract member, a generic interface, overriding a member of `object`, a virtual or abstract property, a record in a hierarchy, a base class or interface the file does not declare, a field initializer of a derived class that is not a constant, a conditional of objects of two classes | - | `PLY1002` naming it |
| a generic method of a class, a generic class in a hierarchy ([generics](generics.md)), an event, an indexer, an operator, a finalizer, a static constructor, a static property, an accessor with its own accessibility, `with`, a string field, an object initializer inside an expression | - | `PLY1002` naming it |

## JavaScript frontend

| Construct | IR | Status |
| --- | --- | --- |
| `class C { constructor(){…} m(){…} get p(){…} set p(v){…} static s(){…} }` | `IrClassType` | supported |
| `f = e;`, `#f`, `#m()`, `static f = e;` | fields (a private one `_f`), private members, owned globals | supported |
| `function C(a) { this.a = a; }` with `C.prototype.m = function …`, `Object.defineProperty(C.prototype, "p", { get, set })`, `C.s = function …`, `C.k = value` at the top level | `IrClassType` | supported |
| `new C(…)`, `this.f`, `this.m(…)`, `C.s(…)`, `o.p = v` through a setter | `IrNew`, field and method nodes | supported |
| `null`; `===`/`!==` between objects or with `null` | `IrNull`, `IrReferenceEquals` | supported |
| `class B extends A`, `super(…)` as the first statement, `super.m(…)`, `super.p` of a getter | hierarchy, `Base` calls | supported |
| `B.prototype = Object.create(A.prototype)`, `B.prototype.constructor = B`, `A.call(this, …)` first, `A.prototype.m.call(this, …)` | hierarchy, `Base` calls | supported |
| `x instanceof C` with a class of the file | `IrTypeTest` | supported |
| a variable reached by objects of several classes with a common base class | that base class | supported |
| `get k()` / `set k(v)` of a derived class over the accessors of `k` of its base class (#1368) | getter and setter methods that override, a `virtual` / `override` property | supported |
| `this.k = v` where `k` is a setter of the class or of a base class and no field (#1368) | a call of the setter, no field | supported |
| `class D extends B { … }` without a constructor (#1369) | a constructor of the parameters of the nearest one up the lineage, calling it with them | supported |
| `const C = Object.freeze({ K: { name: '…', level: 1 }, … })` of members of one shape of strings, numbers and truth values (#1371) | a class `C` of read-only fields, `C.K` a new object of its values | supported |
| `Object.keys(o)` of an object of a class or a record, `Object.keys(o).forEach(key => …)`, `for (const key of Object.keys(o))` with `o[key]` and `r[key] = v` (#1373) | the Array of its keys; the body once per key, `o[key]` the field | supported |
| a call of an overridden method on `this` reachable from a constructor, which a class derived from the one constructed implements | - | `PLY1102`: "a call of an overridden method on 'this' during construction" |
| a getter or a setter alone over a pair of its base class, an accessor over a field or a method, members of a frozen object of two shapes, a comparison of them by identity, a write into one, `r[key] = v` of a key `r` lacks, a `return`, `break` or `continue` in a loop over `Object.keys` | - | `PLY1102` naming it |
| `extends` of anything but a class of the file (`Array`, `Error`, a call), `Object.setPrototypeOf`, a hierarchy that loops, a member of a base class declared again in another kind, an override with another number of parameters, a derived class without a constructor whose base constructor takes a rest parameter, a derived constructor that does not begin with the base call | - | `PLY1102` naming it |
| a computed member name, a static block, prototype mutation, a property added outside the constructor (`__proto__` among them), `delete`, a method taken from its object, `call`/`apply`/`bind` of a method, `this` outside a method, the class used as a value, a field with a value in a class without a constructor | - | `PLY1102` naming it |
| a field or a variable that values of two kinds reach, objects of two unrelated classes among them | - | `PLY1105` |

The fields of a class are those it declares and those its constructor assigns through `this`, in the
order the source first names them. The type of a field is the join of every value stored into it
anywhere in the file: a double unless the program proves an integer, as for a variable; `null` joins
with a reference into that reference. A field may hold a string, an object, an array, or an `Array`
of strings, objects or arrays, which is a list (#1365): `[new Item(t)]` is a list of one element,
and a `push`, `pop`, `splice` or `length = 0` through the field - of `this` or of another object -
makes the field a list as it does a variable. An object, an array or a function counts as true
where it is not `null`; a list, which the IR never holds `null`, as true. A parameter of a method of an exported class is called from
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
| class with a derived class / abstract / leaf | `public class C : B, I` / `public abstract class` / `public sealed class` | same | same | same | same |
| virtual / override / sealed override / abstract method | `public virtual`, `public override`, `public sealed override`, `public abstract T M();` | same | same | same | same |
| interface; implementation | `public interface I : J { T M(); T P { get; } }`; a public method | same | same | same | same |
| base call, base constructor | `base.M(…)`, `: base(…)` (left out without arguments) | same | same | same | same |
| `IrTypeTest`, `IrAsCast`, `IrCast` | `x is T`, `x as T`, `(T)x` | same | same | same | same |
| a test of a variable whose branch declares it cast | `if (x is T) { T t = (T)x; … }` | same | same | `if (x is T t) { … }` (C# 7) | same |
| `IrTrap.InvalidCast` | the runtime's own `InvalidCastException` | same | same | same | same |

Every row is a capability row (`auto-implemented property`, `get-only auto-property`, `init accessor`,
`record class`, `pattern declaration`, the existing `expression-bodied member`); the rows of the
hierarchies exist from C# 1 on but the pattern declaration of C# 7; the init accessor and the record need the
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
| derived class | `function B(…) { A.call(this, …); … }`, then `B.prototype = Object.create(A.prototype); B.prototype.constructor = B;` | `class B extends A { constructor(…) { super(…); … } }` | same |
| virtual, override, abstract method | a method on the prototype; an abstract one is not written | a method of the class body; abstract not written | same |
| base call | `A.prototype.m.call(this, …)` | `super.m(…)` | same |
| `protected` member | `_`-prefixed property | same | same (`#` would hide it from a subclass) |
| interface | not written; `Object.defineProperty(C.prototype, "$is_I", { value: true })` on every class that implements it | same | same |
| `IrTypeTest` of a class / an interface | `x instanceof C` / `x != null && x.$is_I === true` (`$is_I(x)` for an operand that is no variable) | same | same |
| `IrAsCast` | `x instanceof C ? x : null` (`$as_C(x)` for an operand that is no variable) | same | same |
| `IrCast` | `$cast_C(x)`: `x` for `null` or an object of the type, else `throw new TypeError("invalid cast")` | same | same |
| several constructors of a derived class | `$init_<Class><k>` methods | `PLY1152`: `super(…)` cannot stand in a method | same |
| public class | `exports.C = C` (cjs), the global's property (iife) | `export class C` (esm) | same |

The rows are capability rows: `class` (2015, replacement `function`), `class accessor` (2015,
`Object.defineProperty`), `class fields` (2022, constructor assignments), `#private` (2022, `_name`),
`static fields` (2022, assignment after the class), `extends` (2015, `Object.create` chain), `super`
(2015, `Base.prototype.m.call(this, …)`), `Object`, `Object.defineProperty`, `Object.create`,
`instanceof`, `.call` and `TypeError` (5). Constructors of one arity cannot be told apart by
JavaScript, and several constructors of a derived class need `super(…)` in a method at the levels
with classes; both are `PLY1152`, nothing else is.

**Decision (privacy below ES2022).** A private member is an ordinary property with a leading
underscore at es5…es2020. The translated program cannot observe the difference; a consumer of the
exported class can, which the header of the file states
(`// Private members are '_'-prefixed properties at this level.`). A `WeakMap`-based emulation is not
used: it changes the cost of every field access.

## Inheritance, interfaces and dispatch

A class derives from at most one class of the module (`IrClassType.Base`) and implements interfaces
(`IrInterfaceType`: methods and properties without bodies and the interfaces it extends). An object
of a derived class has the fields of its base classes first and is an object of each of them and of
every interface they implement; a reference converts to any of these implicitly and without an
effect (`IrType.IsAssignableTo`). The semantics are stated once, at `IrMethodCallKind`:

- **Dispatch.** A method is `Static`, `Instance`, `Virtual`, `Abstract` or an `Override` of a method
  of a base class (`IrMethod.Dispatch`, `Overrides`, `IsSealedOverride`); a method and its overrides
  have one signature. A `Direct` call runs the method named, a `Virtual` one the nearest override of
  the receiver's class (`IrClassType.Select`), an `Interface` one the implementation the receiver's
  class or a base class gives (`IrMethod.Implements`, `IrClassType.Implementation`), and a `Base`
  call the method of the base class named, on `this`. An accessor is `Virtual` or an `Override`
  where a derived class declares the getter or setter of its key again (#1368), never `Abstract`;
  the C# property is as `virtual` or `override` as its accessors.
- **Construction.** An object is made base first: a constructor of a derived class begins with the
  call of a constructor of its base class (or of another of its own), then writes the field
  initializers of its class. C# runs those initializers before the base constructor, JavaScript after
  `super()`: an initializer of a derived C# class is therefore admitted only when it is a constant.
- **The constructor rule.** While any constructor of an object runs, no virtual or interface call is
  made on it, directly or through a callee, a local or an argument (`IrConstruction`), that a class
  derived from the constructor's own class implements. A call that only the class itself or a base
  class implements selects a method whose fields have their values in both languages by then (#1368):
  `D() { super(); this.key = k; }` of a class that nothing derives from calls its own setter. That is where
  the two languages differ: with .NET 10 and node 22, `Base() { Init(); }` with a `Derived.Init` that
  reads a field initialized to 42 gives 42 in C# and `undefined` in JavaScript (and a class field
  `seen = -1` overwrites what `Init` wrote). No emitted form reproduces the other order, so the
  verifier refuses such a call and both frontends report it at the call.
- **Type tests.** `IrTypeTest` is false for `null`; `IrCast` lets `null` and objects of the type pass
  and traps with `IrTrap.InvalidCast` before anything later is evaluated; `IrAsCast` yields `null`
  instead.

The verifier checks that a base class and an interface are of the module, that no class derives
from a sealed class or from itself, that an override replaces a virtual method of a base class with
its name and signature, that an abstract method stands in an abstract class and has no body, that a
class that is not abstract implements every abstract and every interface method, and that each call
kind names a method it can name.

**JavaScript.** Every method of a JavaScript class is virtual; in the IR it is `Virtual` only where a
derived class of the file overrides it, so that the C# output marks it `virtual` there and nowhere
else. The parameters and results of a method and its overrides are inferred jointly: in `shapes.js`
every `area` is a double, because a product is never narrowed.

## Known differences

- .NET evaluates the arguments of a call on `null` before it throws (`callvirt`); the IR and
  JavaScript trap first. The difference is visible only through state a later call of the same
  instance reads, since nothing in the IR catches a trap yet.
- A static field of a class keeps no visibility of its own; both backends write it public.
- A JavaScript field declared without a value and never assigned holds the IR's default (0, false,
  `null`) where JavaScript holds `undefined`.
- Mutable object literals of JavaScript (written after construction) stay refused; they wait for a
  class synthesized from their shape.
- An interface marker `$is_I` and a `protected` member `_m` are visible to a consumer of the
  exported JavaScript class; the translated program cannot observe either.
- Arrays of references (`Shape[]`) are not in the IR yet; the dispatch fixtures pass shapes through
  parameters of the base class instead.
- `object` as a static type is refused: a value of any class of the file as `object` waits for the
  common root that generics bring.
