# Backends: tree, passes, printer

**Status:** implemented for the C# and the JavaScript backend of the prototype ([#1336](https://github.com/Hawkynt/Polypiler/issues/1336)), for the Python backend ([Python backend](PYTHON-BACKEND.md)), for the Rust backend ([Rust backend](RUST-BACKEND.md)), for the Go backend ([Go backend](GO-BACKEND.md)), for the Kotlin backend ([Kotlin backend](KOTLIN-BACKEND.md)) and for the classic Visual Basic backend ([classic Visual Basic backend](VISUALBASIC-BACKEND.md)); stage 8 of the [pipeline](PIPELINE.md). The helpers are one IR library since [#1353](https://github.com/Hawkynt/Polypiler/issues/1353).

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

| | C# | JavaScript | Python | Rust | Go | Kotlin |
| --- | --- | --- | --- | --- | --- | --- |
| Tree | `Polypiler.Core.Backends.CSharp/Syntax/CSharpSyntax.cs`: compilation unit, namespaces, types, members, statements, expressions | `Polypiler.Core.Backends.JavaScript/Syntax/JavaScriptSyntax.cs`: ESTree-shaped program, statements, expressions | `Polypiler.Core.Backends.Python/Syntax/PythonSyntax.cs`: module, statements, expressions after CPython's `ast` | `Polypiler.Core.Backends.Rust/Syntax/RustSyntax.cs`: files, items, statements, expressions; a crate is `RustCrate` | `Polypiler.Core.Backends.Go/Syntax/GoSyntax.cs`: file, declarations, statements, expressions after `go/ast` | `Polypiler.Core.Backends.Kotlin/Syntax/KotlinSyntax.cs`: file, declarations, statements, expressions after the grammar of the Kotlin specification |
| Printer | `CSharpPrinter.Print(CsCompilationUnit, CSharpProfile)` | `JavaScriptPrinter.Print(JsProgram, JavaScriptProfile)` | `PythonPrinter.Print(PyModule)` | `RustPrinter.Print(RsFile)`, rustfmt's default layout | `GoPrinter.Print(GoFile)`, gofmt's layout | `KotlinPrinter.Print(KtFile)`, the Kotlin coding conventions |
| Reader | `CSharpSyntaxReader` (Roslyn) | `JavaScriptSyntaxReader` (Acornima) | `PythonSyntaxReader` (its own, for the subset it prints) | `RustSyntaxReader` (recursive descent over the printed subset) | `GoSyntaxReader` (recursive descent over the printed subset) | `KotlinSyntaxReader` (recursive descent over the printed subset) |
| Passes | `CSharpTargetPasses.Pipeline` | `JavaScriptTargetPasses.Pipeline` | `PythonTargetPasses.Pipeline` | `RustTargetPasses.Pipeline` | `GoTargetPasses.Pipeline` | `KotlinTargetPasses.Pipeline` |
| Tree after the passes | `CSharpEmitter.Build(…)` | `JavaScriptEmitter.Build(…)` | `PythonEmitter.Build(…)` | `RustEmitter.Build(…)` | `GoEmitter.Build(…)` | `KotlinEmitter.Build(…)` |

The language-specific guides describe the profiles and lowering contracts of the
[C#](CSHARP-BACKEND.md), [JavaScript](JAVASCRIPT-BACKEND.md), [Python](PYTHON-BACKEND.md),
[Rust](RUST-BACKEND.md), [Go](GO-BACKEND.md) and [Kotlin](KOTLIN-BACKEND.md) backends.

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
| `Polypiler.Core.Backends.VisualBasic/Helpers/VisualBasicHelpers.cs` | The IR's operations written with what classic Visual Basic has, where every integer operation traps: the 32-bit sum, difference, product and negation modulo 2^32 computed exactly in `Double`, the unsigned value of a `Long`'s bits and its division, remainder, checked arithmetic and shifts, the signed division that LibreOffice cannot do at `&H80000000 \ -1`, the narrowing conversions that wrap or check, the roundings and the string operations by code unit with their traps. Each is the operation itself on the target's representation of a number, which the IR has no weaker operation to write it with. |
| `Polypiler.Core.Backends.VisualBasic/Helpers/VisualBasicHelpers.cs` (`ArrayHelpers`) | The length, creation, copy and fill of a dynamic array per element type: an empty Visual Basic array has no bounds, and `ReDim` makes none - a representation of the target. |
| `Polypiler.Core.Backends.JavaScript/Helpers/JavaScriptHelpers.cs` | The level's own form of the IR's operations: `$imul`, `$fround` and `$trunc` below ECMAScript 2015; with `BigInt` the division that traps (`$div64` to `$urem64`) and the conversions from floating point (`$f2l`, `$f2lc`, `$f32`) and the range check of an arbitrary-precision integer (`$bigfit`); the bounded access, creation, filling, copying and step of an array, plain or typed (`$get`, `$set`, `$new`, `$len`, `$fill`, `$copy`, `$step` and its forms); the bits of a number through a `DataView` (`$bits64`, `$float64`), and of a pair of halves as text (`$key64`). Each is the operation itself on the target's representation, for which the IR has no weaker operation to write it with; the 64-bit arithmetic on pairs of halves (O0075) and the traps of the arithmetic on numbers are helpers of the library. |
| `Polypiler.Core.Backends.JavaScript/Helpers/HelperTexts.cs` | The notation of the helpers that stay: one text, or one for a `BigInt` and one for the halves. |
| `Polypiler.Core.Backends.JavaScript/Helpers/Errors.cs` | The error classes the exception classes of the IR become, and the helpers that throw and chain them: a representation of the target. |
| `Polypiler.Core.Backends.JavaScript/Helpers/Optional.cs` | How an optional value that holds none is spelled at the boundary of JavaScript: a representation. |
| `Polypiler.Core.Backends.JavaScript/Helpers/Views.cs` | A view is a `subarray` or an object `{ a, o, n }` over a plain array: its helpers work on that representation. |
| `Polypiler.Core.Backends.JavaScript/Helpers/ViewBytes.cs` | The bytes of a view over a plain array, by the index into the array beneath: what the view's endian access calls. |
| `Polypiler.Core.Backends.JavaScript/Helpers/Collections.cs` | The slots of `$Keyed`, a map or set below ECMAScript 2015, and the list operations of the arrays the lists are: a representation. |
| `Polypiler.Core.Backends.JavaScript/Helpers/Sequences.cs` | The cursor of a lazy sequence below ECMAScript 2015 - `$generator`, `$closeAll`, `$iterate`, `$arrayIterator` - is an object with `next()` and `"return"()` that closes over the state machine and its pending `finally` blocks: the iterator protocol of ECMA-262, a representation of the IR's sequence, which has no type the library could be written in. |
| `Polypiler.Core.Backends.JavaScript/Helpers/Pointers.cs` | A pointer is a region object `{ a, o, n, s, k }` and a byte offset, its loads and stores of another kind go through a `DataView` or the bits of a plain array's elements: a representation of the IR's bounded pointer, like the view's. |
| `Polypiler.Core.Backends.JavaScript/Helpers/Async.cs` | `$async` drives the generator an async function is at ECMAScript 2015 on the native `Promise`, `$whenAll` waits for every promise and rejects with the first rejection in argument order (#1330): both work on the target's `Promise`, of which the IR has no type the library could be written in. |
| `Polypiler.Core.Backends.JavaScript/Helpers/Enum/Enums.cs` | The frozen object an enum is, looked up by name and value: a representation. |
| `Polypiler.Core.Backends.JavaScript/Helpers/String/Strings.cs` | The string operations of .NET and of the later levels in JavaScript: operations of the IR, not yet carried into the library (a follow-up of #1353). |
| `Polypiler.Core.Backends.JavaScript/Helpers/String/NumberText.cs` | The numbers to text and back of .NET in JavaScript: operations of the IR, not yet carried into the library (a follow-up of #1353). |
| `Polypiler.Core.Backends.Python/Helpers/PythonHelpers.cs` | The operations of the IR Python has no expression for, written with what Python has: it cannot raise inside an expression, so every operation that traps is a function (the checked range of an integer, the truncating division and remainder, a negative index or array length, the conversions of a float to an integer); its float division and remainder raise where IEEE 754 gives an infinity or a NaN; it has no binary32 type, so rounding to it goes through `struct`; and the operations that have no portable implementation in the IR (square root, roundings, sign, IEEE minimum and maximum) are written as Python computes them exactly. Each is the operation itself, for which the IR has no weaker operation to write it with. |
| `Polypiler.Core.Backends.Python/Helpers/PythonHelpers.Objects.cs` | The object model where Python has no expression: the checked cast (`_ply_cast`), an exception with its cause and its message, an assignment to a cell inside an expression, a raise where a value stands, the unwrap of an optional value, the orders and equalities of generic definitions, a dict key of a NaN, an array of distinct structs, the restartable sequence `_PlySequence` (an object whose every enumeration calls the generator function anew, as the IR's restartable sequence is), the names of an enum's values as .NET writes and parses them, and ECMA-262's `ToInt32`/`ToUint32`: representations of the target and operations of the IR not yet carried into the library. |
| `Polypiler.Core.Backends.Python/Helpers/PythonHelpers.Strings.cs` | The string operations of the IR where Python would count from the end, clamp, split or trim otherwise, on a `str` of UTF-16 code units: operations of the IR, not yet carried into the library (a follow-up of #1353). |
| `Polypiler.Core.Backends.Python/Helpers/PythonHelpers.Collections.cs` | The list operations that check an index, the searches by `Equals`, the keyed operations that trap, the walks that trap after a change as .NET's enumerators do, `_PlySlots` (a `dict` in the order of .NET's slots) and `_PlyList` (a `list` that counts its changes): representations of the target. |
| `Polypiler.Core.Backends.Python/Helpers/PythonHelpers.Views.cs` | A view is a `_PlyView` of a list, its first element and a length, or of the bytes of a list read with a `struct` format: the representation of the IR's span and memory, which Python has no type for, with the bounded copies, fills and comparisons and the bytes of the endian accesses. |
| `Polypiler.Core.Backends.Python/Helpers/PythonHelpers.Pointers.cs` | A pointer is a `_PlyPointer` of a region and a byte offset, its loads and stores of another type go through the bytes of the elements they cover: a representation of the IR's bounded pointer, like the view's. |
| `Polypiler.Core.Backends.Python/Helpers/PythonHelpers.NumberText.cs` | The numbers to text and back of .NET and of ECMA-262 in Python: operations of the IR, not yet carried into the library (a follow-up of #1353). |
| `Polypiler.Core.Backends.Kotlin/Helpers/KotlinHelpers.cs` | The semantics of the IR Kotlin's operators state otherwise, each a private function built as a tree: the signed quotient and remainder that trap on the smallest value divided by -1 (Kotlin wraps), the checked sum, difference and product of the unsigned types and of the 8- and 16-bit steps (`Math.addExact` takes `Int` and `Long` only), the checked conversions between integers and from floating point, the `u64` to `f32` conversion Kotlin rounds twice, the sign of a floating-point number that traps on a NaN, the rounding with ties away from zero, and the copy between arrays that traps before it copies: operations of the IR on the representation of the target, for which the IR has no weaker operation. |
| `Polypiler.Core.Backends.Rust/Helpers/BigInts.cs` | The arbitrary-precision integer has no type in Rust's standard library: `PlyBigInt`, a sign and 32-bit digits, is the representation of the IR's `bigint`, and its functions (`ply_big`, `ply_big_parse`, `ply_big_add`, `ply_big_sub`, `ply_big_mul`, `ply_big_div`, `ply_big_rem`, `ply_big_and`, `ply_big_or`, `ply_big_xor`, `ply_big_neg`, `ply_big_not`, `ply_big_shift`, `ply_big_low`, `ply_big_fit`, `ply_big_to_f64`, `ply_big_of_f64` and the magnitude functions they call) are the operations themselves on it, converted from num-bigint (MIT, `THIRD-PARTY-NOTICE.num-bigint.txt`); the IR has no type the library could write them in. |
| `Polypiler.Core.Backends.Rust/Helpers/Strings.cs` | A string of Rust is its UTF-16 code units, `Vec<u16>` or `&[u16]`, since `str` is UTF-8 and holds no lone surrogate: the searches, the split, the trim with the IR's white-space tables, the padding and the replacements work on that representation, which the IR has no type for (`ply_utf16`, `ply_position`, `ply_index_of`, `ply_last_index_of`, `ply_replace`, `ply_replace_first`, `ply_split`, `ply_trim`, `ply_pad`). The same file holds the other forms of Rust's own the backend writes once: `ply_non_null` and `ply_value` read an `Option` that a test before has to hold a value, `ply_with` holds the `Mutex` of a global for one access, the `ply_list_*`, `ply_map_*`, `ply_set_*` and `ply_slot_*` functions keep a list's checked indices and the order of the slots of .NET's `Dictionary` and `HashSet` on a vector. |
| `Polypiler.Core.Backends.Rust/Helpers/Strings.Results.cs` | The same helpers of `Strings.cs` that trap, in the form the trap policy `result` takes (#1415): the trap as the value, an `Option`, or a `Result` of the error each trap is given, since a panic is no value a caller can turn into an error. The same representation, not another algorithm. |
| `Polypiler.Core.Backends.Go/Helpers/GoHelpers.cs` | The operations of the IR Go has no expression for, written with what Go has: Go's arithmetic never reports an overflow, so the checked sum, difference, product and negation test the result (through `math/bits` on 64 bits); Go's signed division wraps the smallest value divided by -1, which the IR traps on; Go leaves the conversion of a floating-point number outside an integer type to the implementation, so the saturating and checked conversions test the range first; Go has no checked conversion between integers, no `min`/`max` before 1.21, no sign of a float that traps on a NaN and no rounding of halves upwards. Each is the operation itself, for which the IR has no weaker operation to write it with. The emitter also generates `plyEqual<T>`, the equality of a struct with a float field that lets a NaN equal a NaN. |

Outside the folders the emitters keep a few more of the same kind: the C# `Throw<T>` of a throw
expression before C# 7, the C# `SinglePass<T>` that makes the one cursor of an iterator a sequence
enumerated once (a `yield return` over an `IEnumerator<T>`, which the IR has no type for), the
JavaScript `$sameValueZero` and `$compare` of the generic equality and order, and the Rust spelling
functions, built as trees by the emitter: `ply_div_<t>` and `ply_rem_<t>`, the IR's division with its
traps named as every generated panic is, `ply_f2i_trap_<t>`, the checked conversion from floating
point Rust has no method for, `ply_rs_f2i_wrap_<t>`, the wrapping one of ECMA-262's `ToInt32`, `ply_i2i_trap_<from>_<to>`, the checked narrowing below Rust 1.34, and
`ply_range`, `ply_copy` and `ply_copy_within`, the range of a copy or an endian access checked before
anything is read or written, which a slice's own panics would report as an overflow or not at all.
Under the trap policy `result` these return a `Result` instead of panicking, and `ply_trap(class)` makes
the exception a trap of a class throws, with the class's default message (ADR 0008).

## Target passes

A pass implements `ITargetPass<TTree, TProfile>` (`Polypiler.Core.Optimize.Target`): an optimization
number `Id`, a `Name` and `Run(tree, profile, applied)`, which returns the rewritten tree and reports
every change. `TargetPipeline` runs the passes in table order, skips those `--disable-opt` names and
runs none at `--opt none`; `--explain-opt` writes every change as a note, as it does for the IR rules,
with which the passes share one namespace of numbers. A pass never prints and never re-parses.

| Number | Pass | What it does |
| --- | --- | --- |
| [O0500](../optimizations/O0500-target-helper-inlining.md) | helper inlining | a call of a single-expression helper becomes its body where the cost rule says so |
| [O0501](../optimizations/O0501-target-unused-declaration-removal.md) | unused declaration removal | a helper or private constant nothing names any more is removed; in Python also an `import` nothing uses; in Kotlin a helper function |

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
  tests of the Python/Perl layer. Kotlin in its oldest and its newest profile is read back by
  `KotlinSyntaxReader` into a tree that prints the same text (`KotlinParseBackTests`); `kotlinc` with
  warnings as errors compiles and the JDK runs the generated files in the differential tests of the
  Kotlin/Java layer (`KotlinDifferentialTests`).
