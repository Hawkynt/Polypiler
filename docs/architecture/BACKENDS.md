# Backends: tree, passes, printer

**Status:** implemented for the C# and the JavaScript backend of the prototype ([#1336](https://github.com/Hawkynt/Polypiler/issues/1336)) and for the Python backend ([Python backend](PYTHON-BACKEND.md)); stage 8 of the [pipeline](PIPELINE.md). The helpers are one IR library since [#1353](https://github.com/Hawkynt/Polypiler/issues/1353).

A backend does not write text. It builds a syntax tree of its target language from the IR, runs the
target passes over that tree and hands the result to its printer, the only place where generated code
becomes text:

```mermaid
flowchart LR
  IR["Legalized IR"] --> Build["Emitter: build the tree<br/>(capability table → tree fragments)"]
  Build --> Passes["Target passes<br/>O0500, O0501, …"]
  Passes --> Print["Printer"]
  Print --> File["Generated file"]
```

| | C# | JavaScript | Python |
| --- | --- | --- | --- |
| Tree | `Polypiler.Core.Backends.CSharp/Syntax/CSharpSyntax.cs`: compilation unit, namespaces, types, members, statements, expressions | `Polypiler.Core.Backends.JavaScript/Syntax/JavaScriptSyntax.cs`: ESTree-shaped program, statements, expressions | `Polypiler.Core.Backends.Python/Syntax/PythonSyntax.cs`: module, statements, expressions after CPython's `ast` |
| Printer | `CSharpPrinter.Print(CsCompilationUnit, CSharpProfile)` | `JavaScriptPrinter.Print(JsProgram, JavaScriptProfile)` | `PythonPrinter.Print(PyModule)` |
| Reader | `CSharpSyntaxReader` (Roslyn) | `JavaScriptSyntaxReader` (Acornima) | `PythonSyntaxReader` (its own, for the subset it prints) |
| Passes | `CSharpTargetPasses.Pipeline` | `JavaScriptTargetPasses.Pipeline` | `PythonTargetPasses.Pipeline` |
| Tree after the passes | `CSharpEmitter.Build(…)` | `JavaScriptEmitter.Build(…)` | `PythonEmitter.Build(…)` |

## The tree

The nodes are immutable records, one per construct the emitter writes. Every expression carries the
IR type it was written for (`Type`; unknown only in the source of a helper that stays with a backend),
so a pass can reason about types.
Parentheses are nodes of their own: the emitter puts them where it always has, which keeps the output
of the old string-building emitters byte for byte. A few properties only say how a node is laid out -
a blank line or comment in front of a statement, a block on one line, a line break before an operator,
a tabulated table, the spelling of a literal in a helper's source. They change no meaning, a rewrite
keeps them only on the nodes it does not replace, and a comparison of two trees ignores them.

`CSharpSyntaxRewriter` and `JavaScriptSyntaxRewriter` walk a tree (`DescendantsAndSelf`) and rewrite it
bottom-up (`Rewrite`); a rewrite that changes nothing returns the same tree.

## Helpers: one library in the IR

What replaces an operation a profile has no native form of - a rotate before .NET Core 3.1 or in any
ECMAScript, a saturating conversion before .NET 9, the IEEE minimum before .NET Core 3.0, the rounding
to even of JavaScript - is a helper of **one library**, `Polypiler.Core.IR/Helpers/Operations`: ordinary
C# in the subset the C# frontend imports, one file per family (`Rotate.cs`, `Counts.cs`, `Division.cs`,
`Saturating.cs`, `Rounding.cs`, ...), compiled into the IR assembly so that every helper runs as .NET
too. The build imports it beside the portable floating-point implementations
(`polypiler-tooling float-implementations`, `ImplementationSources.Merge`) and the executable carries
both as one precomputed module.

Legalization binds what a module needs: `LibraryLegalization` walks every expression - the global
initializers included - and asks the backend's rewrite whether the profile writes it; where it does
not, the node becomes the call of its helper, and the helper, what it calls and the tables it reads are
copied into the module under names nothing else has (`IrModule.Implementations`). `IntegerLibrary`
says once, for every backend, which helper computes an integer or bit operation for its width and
signedness: a helper of 32 bits serves the narrow types with the width as an argument, one of 64 bits
works on `ulong`. The backends only decide where they need it - the C# rows carry `WithLibrary`, the
JavaScript rows (`Library/*.cs`) say which levels lack what. The helpers are then functions of the
module, written through the path every function takes: typed target trees, the profile's syntax, the
passes of the IR before and the target passes after. A helper calls no operation it implements
itself; the few operations C# has no method for are named by stubs (`IntrinsicRoundHalfUp`) the frontend
imports as the operation.

Two families take the place of what the JavaScript backend once wrote as text. Below ECMAScript 2020,
which has no `BigInt`, a 64-bit integer is two 32-bit limbs ([O0075](../optimizations/O0075-wide-integer-legalization.md),
`WideIntegerLibrary`): the IR's `HighWord` and `JoinWords` take a value apart and put it together, and
every operation on 64 bits - arithmetic, shifts, orders, division, the checked forms, the conversions
from and to floating point, steps - is a helper of `Limbs.cs` written with them. The traps of the
arithmetic on numbers, which do not trap in JavaScript, are helpers of `Traps.cs` (`TrapLibrary`): the
division and remainder, the checked sum, difference, product and negation, the checked conversions and
those from floating point - a check that throws the IR's exception class, then the operation on exact
numbers through `ToInt32` or `ToUint32`. Once inlined where an operand is a constant, folding removes
the check it proves: `x / 3` is `(x / 3) | 0`.

The passes over the legalized module ([pipeline](PIPELINE.md)) make a helper cheap where its arguments
say more than its signature: `ushort.RotateLeft(x, 3)` becomes the call of `RotateLeftNarrow(x, 3, 16)`,
which inlining (O0006), propagation (O0003), folding (O0001) and dead-code removal (O0005) turn into
`(x << 3) | (x >> 13)` with no branch and no call; unrolling (O0023) takes the loop of a 64-bit load
apart into its eight bytes.

The licences stay attached: a helper's file states its licence in its `SPDX-License-Identifier`
header, the output's header names it, and the notices a file obliges an output to keep (the FreeBSD
BSD-2-Clause of the `float` fused multiply-add) stay in the output also where the function was inlined
and is gone (`IrModule.Inlined`).

A row of `CSharpCapabilities` says its native form in C# source - a method, a cast, further arguments
or a template with `{0}`, `{1}` - and `CSharpForm.Build` makes it a tree fragment. The JavaScript table
names what each level has; the emitter makes the fragment for it.

## Helpers that stay with a backend

Some helpers are no operation of the IR but the way a target represents one, or the operation itself
written with what a level has; they stay text of their backend, read into the tree once. Each file
and why:

| File | Why it stays |
| --- | --- |
| `Polypiler.Core.Backends.CSharp/Helpers/CSharpHelper.cs` | The type of a C# helper written as source lines, which the exceptions of the C# backend are made of. |
| `Polypiler.Core.Backends.CSharp/Helpers/CSharpHelpers.cs` | `Ignore<T>` stands in for the discard C# 7 brought, `Fill<T>` and `Clear` for `Array.Fill` and `Array.Clear` on any element type: a generic of the target's language, where the library has a helper per type. |
| `Polypiler.Core.Backends.CSharp/Helpers/Views.cs` | Below .NET Standard 2.1 a view is an `ArraySegment<T>`; its helpers work on that representation, which the IR does not see. |
| `Polypiler.Core.Backends.CSharp/Helpers/Collections/CSharpCollectionClasses.cs` | The nested classes the keyed collections are where .NET's do not order or compare as the IR says: a representation of the target. |
| `Polypiler.Core.Backends.CSharp/Helpers/String/CSharpStringHelpers.cs` | The string operations of ECMA-262 in C#: operations of the IR, not yet carried into the library (a follow-up of #1353). |
| `Polypiler.Core.Backends.CSharp/Helpers/String/CSharpNumberTextHelpers.cs` | The numbers to text and back of ECMA-262 in C# (Ryu, V8): operations of the IR, not yet carried into the library (a follow-up of #1353). |
| `Polypiler.Core.Backends.CSharp/Helpers/BigIntegers.cs` | `BigToDouble`: the IR's conversion of an arbitrary-precision integer to `f64` rounds to nearest, where .NET's own `(double)` of a `BigInteger` cuts the bits it does not keep - the conversion itself on the target's representation, which the library cannot write without a `BigInteger` of its own subset. |
| `Polypiler.Core.Backends.JavaScript/Helpers/JavaScriptHelpers.cs` | The level's own form of the IR's operations: `$imul`, `$fround` and `$trunc` below ECMAScript 2015; with `BigInt` the division that traps (`$div64` to `$urem64`) and the conversions from floating point (`$f2l`, `$f2lc`, `$f32`) and the range check of an arbitrary-precision integer (`$bigfit`); the bounded access, creation, filling, copying and step of an array, plain or typed (`$get`, `$set`, `$new`, `$len`, `$fill`, `$copy`, `$step` and its forms); the bits of a number through a `DataView` (`$bits64`, `$float64`), and of a pair of halves as text (`$key64`). Each is the operation itself on the target's representation, for which the IR has no weaker operation to write it with; the 64-bit arithmetic on pairs of halves (O0075) and the traps of the arithmetic on numbers are helpers of the library. |
| `Polypiler.Core.Backends.JavaScript/Helpers/HelperTexts.cs` | The notation of the helpers that stay: one text, or one for a `BigInt` and one for the halves. |
| `Polypiler.Core.Backends.JavaScript/Helpers/Errors.cs` | The error classes the exception classes of the IR become, and the helpers that throw and chain them: a representation of the target. |
| `Polypiler.Core.Backends.JavaScript/Helpers/Optional.cs` | How an optional value that holds none is spelled at the boundary of JavaScript: a representation. |
| `Polypiler.Core.Backends.JavaScript/Helpers/Views.cs` | A view is a `subarray` or an object `{ a, o, n }` over a plain array: its helpers work on that representation. |
| `Polypiler.Core.Backends.JavaScript/Helpers/ViewBytes.cs` | The bytes of a view over a plain array, by the index into the array beneath: what the view's endian access calls. |
| `Polypiler.Core.Backends.JavaScript/Helpers/Collections.cs` | The slots of `$Keyed`, a map or set below ECMAScript 2015, and the list operations of the arrays the lists are: a representation. |
| `Polypiler.Core.Backends.JavaScript/Helpers/Sequences.cs` | The cursor of a lazy sequence below ECMAScript 2015 - `$generator`, `$closeAll`, `$iterate`, `$arrayIterator` - is an object with `next()` and `"return"()` that closes over the state machine and its pending `finally` blocks: the iterator protocol of ECMA-262, a representation of the IR's sequence, which has no type the library could be written in. |
| `Polypiler.Core.Backends.JavaScript/Helpers/Pointers.cs` | A pointer is a region object `{ a, o, n, s, k }` and a byte offset, its loads and stores of another kind go through a `DataView` or the bits of a plain array's elements: a representation of the IR's bounded pointer, like the view's. |
| `Polypiler.Core.Backends.JavaScript/Helpers/Enum/Enums.cs` | The frozen object an enum is, looked up by name and value: a representation. |
| `Polypiler.Core.Backends.JavaScript/Helpers/String/Strings.cs` | The string operations of .NET and of the later levels in JavaScript: operations of the IR, not yet carried into the library (a follow-up of #1353). |
| `Polypiler.Core.Backends.JavaScript/Helpers/String/NumberText.cs` | The numbers to text and back of .NET in JavaScript: operations of the IR, not yet carried into the library (a follow-up of #1353). |
| `Polypiler.Core.Backends.Python/Helpers/PythonHelpers.cs` | The operations of the IR Python has no expression for, written with what Python has: it cannot raise inside an expression, so every operation that traps is a function (the checked range of an integer, the truncating division and remainder, a negative index or array length, the conversions of a float to an integer); its float division and remainder raise where IEEE 754 gives an infinity or a NaN; it has no binary32 type, so rounding to it goes through `struct`; and the operations that have no portable implementation in the IR (square root, roundings, sign, IEEE minimum and maximum) are written as Python computes them exactly. Each is the operation itself, for which the IR has no weaker operation to write it with. |

Outside the folders the emitters keep a few more of the same kind: the C# `Throw<T>` of a throw
expression before C# 7, the C# `SinglePass<T>` that makes the one cursor of an iterator a sequence
enumerated once (a `yield return` over an `IEnumerator<T>`, which the IR has no type for), the
JavaScript `$sameValueZero` and `$compare` of the generic equality and order.

## Target passes

A pass implements `ITargetPass<TTree, TProfile>` (`Polypiler.Core.Optimize.Target`): an optimization
number `Id`, a `Name` and `Run(tree, profile, applied)`, which returns the rewritten tree and reports
every change. `TargetPipeline` runs the passes in table order, skips those `--disable-opt` names and
runs none at `--opt none`; `--explain-opt` writes every change as a note, as it does for the IR rules,
with which the passes share one namespace of numbers. A pass never prints and never re-parses.

| Number | Pass | What it does |
| --- | --- | --- |
| [O0500](../optimizations/O0500-target-helper-inlining.md) | helper inlining | a call of a single-expression helper becomes its body where the cost rule says so |
| [O0501](../optimizations/O0501-target-unused-declaration-removal.md) | unused declaration removal | a helper or private constant nothing names any more is removed; in Python also an `import` nothing uses |

The emitter declares only the helpers the code calls (and those they call); a function legalization
copied in from the library is a helper to these passes as well. O0501 removes what later passes made
unused. Most helpers are inlined before, in the IR, by [O0006](../optimizations/O0006-inlining.md),
which is not limited to one expression; O0500 takes what O0006 left - a call it was told not to inline,
a helper over its budget whose body is one expression.

### Adding a pass

1. Take a number of the "Target emission" family of the [catalog](../optimizations/README.md), write
   its page and tick it.
2. Implement `ITargetPass` for each backend it applies to and add it to the backend's pipeline in the
   order it must run.
3. Test it test-first on hand-built trees and on fixtures, both with it on and off; with `--opt none`
   the golden outputs (`tests/fixtures/backend/golden`) must not change.

## Checks

- **Golden outputs:** every source of `tests/fixtures/prototype` in every profile, module format,
  namespace form and floating-point mode, as the emitters wrote them before the trees, recorded by
  `dotnet test Polypiler.Tests -c Release --filter "FullyQualifiedName~GoldenOutputRecorder"`; with
  `--opt none` the backends must reproduce every byte (`GoldenOutputTests`).
- **Parse-back:** every generated file is parsed by Roslyn at the profile's language version and by
  Acornima at the profile's ECMAScript version, read into the tree and compared with the tree it was
  printed from, parentheses and layout aside (`ParseBackTests`). Every prototype source translated to
  Python in its oldest and its newest profile is read back by `PythonSyntaxReader` and compared the
  same way (`PythonParseBackTests`); CPython itself runs the generated modules in the differential
  tests of the Python/Perl layer.
