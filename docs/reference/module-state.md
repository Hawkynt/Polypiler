# Module state

State that the functions of a module share and that lasts from one call to the next: a `static`
field of a C# class, a variable at the top level of a JavaScript module. The IR states the
semantics once (`IrGlobal` in `Polypiler.Core.IR`), the interpreter keeps them per instance of a
module, and both backends write them in every target profile and module format.
Related: [repository README](../../README.md) · [IR and analysis](../architecture/IR.md) ·
[value records](records.md) · [integer and bit operations](integer-operations.md).

## Semantics

- **One instance, one state.** The globals of a module instance - a loaded C# class, an imported
  JavaScript module, an `IrInterpreter.Instantiate` - are initialised once, in source order, before
  any function of the instance runs. Every function sees and changes the same values. There is one
  thread.
- **Static initializers.** An initializer is a constant, operators on constants and on globals
  declared before it, a new array of such a length, an array of listed values, or a call of a
  function of the module that is *pure*: it reads and writes no global, directly or through the
  functions it calls. So an initializer never observes a global that is not initialised yet, and the
  order in which a language runs the initializers of its module - .NET's `beforefieldinit` class
  initialisation, ECMA-262 §16.2's evaluation of a module - cannot be told apart.
- **Default.** A global without an initializer starts as zero or false; an array global has an
  initializer.
- **Arrays.** An array global holds one array for the whole life of the instance: its elements
  change, the array is never replaced. Passed to a function, it is passed by reference, as every
  array is.
- **Arithmetic.** A global wraps around and converts exactly as a local of its type does; a compound
  assignment evaluates the global once.

## Nodes and verifier rules

| Node | Meaning | Verifier |
| --- | --- | --- |
| `IrGlobal(Name, Type, Initializer)` in `IrModule.Globals` | a variable of the module, in source order | name unique among functions, constants and globals; a truth value, a number or an array of numbers; an array has an initializer; the initializer is static and of the global's type |
| `IrGlobalRef(Global)` | the value of a global, of an array global its array | a global of the module; in an initializer one declared before |
| `IrGlobalAssign(Global, Value)` | a statement: a new value | a global of the module that is no array; the value has its type |
| `IrStep` on an `IrGlobalRef` | `++`/`--` of a global | as for a local |

`IrVerifier.Impure(module)` names the functions that read or write a global, directly or through
calls. To the recognition rules a read of a global is an effect: no rule moves, merges or drops it,
and the code they rewrite computes with and without them alike.

## C# frontend

| Construct | Becomes | Status |
| --- | --- | --- |
| `private`/`internal` `static` field of a supported type, with or without an initializer | a global | supported |
| `static readonly` field whose initializer is no constant, or an array one whose elements a method writes (assignment, compound assignment, `++`/`--`, `Array.Fill`/`Clear`, the target of `Array.Copy`) | a global | supported; a `static readonly` table only read stays a constant table |
| reads, assignments, `++`, `--`, compound assignments, also as `Class.field` | `IrGlobalRef`, `IrGlobalAssign`, `IrStep` | supported |
| a local or parameter of the name of a field | hides the field, as in C# | supported; the backend renames what would clash |
| `public` mutable static field (a `public static readonly` array whose elements are written as well) | — | `PLY1002`: JavaScript code that imports a module cannot assign its exports, and the field made private would no longer be what the class shows |
| initializer that reads a later field or its own, or calls a method that reads or writes a static field | — | `PLY1002` |
| static constructor, static property, an attribute on a field (`[ThreadStatic]`), `volatile`, `Interlocked`, `lock` | — | `PLY1002` |
| array field that is assigned, or has no initializer; a static field of a struct type | — | `PLY1002` |

## JavaScript frontend

A variable of the module is typed like a local, from every value that reaches it in any imported
function and in its initializer.

| Construct | Becomes | Status |
| --- | --- | --- |
| `let`/`var` at the top level of the module, with or without an initializer | a global | supported |
| `const` at the top level holding a typed array or an array literal whose elements the file writes | an array global | supported; one only read stays a constant table |
| `let x;` read by a function before that function can have assigned it | — | `PLY1106`, as for a local |
| `export let`, `export var`, `export { x }`, `export default x` | — | `PLY1102` ("the exported variable x") |
| a statement other than a declaration at the top level of a module whose functions use its variables | — | `PLY1102` ("a statement at the top level of the module"); a module without such variables ignores its top level, as before |
| initializer that reads a later variable (temporal dead zone) or calls a function that reads or writes one | — | `PLY1102` |
| a variable holding an object literal | — | `PLY1102` |

A function an initializer calls is imported with the module.

## C# backend

The same form in every profile from `net2.0` to `net10.0`: a private static field of the generated
class, initialised in its declaration, with no static constructor, so that the class keeps
`beforefieldinit`. An array global is `readonly`. A global without an initializer is given its zero
all the same, because the compiler warns about a field nothing assigns (CS0649). An initializer that
wraps around stands in `unchecked(...)`. A name that is a keyword is written with `@`; a local named
like a global is renamed, the global keeps its name.

```csharp
private static uint _state = 2463534242;

private static int _calls = 0;

private static readonly uint[] _buckets = new uint[4];
```

## JavaScript backend

The globals stand after the constants and before the functions, so they are private to the module
in every format: at the top of an ES module, in the body of a CommonJS module after `"use strict"`,
inside the function of a script (`--module iife`), which returns only the functions.

| Level | Scalar | 64-bit | Array |
| --- | --- | --- | --- |
| ES5 | `var state = 2463534242;` | `var s = { hi: 20529293, lo: 3418323524 };` | `var t = $new($len(4), 0);`, a typed array with `--typed-arrays` |
| ES2015, ES2017 | `let state = 2463534242;` | `let s = { hi: 20529293, lo: 3418323524 };` | `const t = new Uint32Array($len(4));` |
| ES2020, ES2022 | `let state = 2463534242;` | `let s = 88172645463325252n;` | `const t = new Uint32Array($len(4));` |

A 64-bit global below ES2020 is the object `{ hi, lo }` of its two halves, as every 64-bit value is
there; the helpers that compute with it return new objects, so the global is replaced on every
assignment and never changed in place. A global that a parameter or a local of some function would
hide is written with `$g` appended.

## Fixtures and checks

`tests/fixtures/prototype/state` holds Marsaglia's xorshift32 (shifts 13, 17, 5; seed 2463534242)
with a call counter, xorshift64 (13, 7, 17; seed 88172645463325252), a histogram in an array global
and a CRC-32 whose table the first call fills, each in C# and in JavaScript. `ModuleStateTests` calls
every one in sequence in one fresh load - a node import in every level and module format, a build of
the C# against every profile's reference assemblies - and compares with the published values: the
first three numbers 723471715, 2497366906, 2064144800 and 8748534153485358512, 3040900993826735515,
3453997556048239312; CRC-32 of "123456789" = 3421780262 on the first and the second call.

## Not yet

Instance state, static properties and static constructors (classes and instances), public mutable
state and live-binding exports, threads and atomics, and module graphs of several files.
