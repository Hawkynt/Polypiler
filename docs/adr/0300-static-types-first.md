# ADR 0300 — Static types first, dynamic last

Area: de-dynamicization

| | |
| --- | --- |
| Status | accepted |
| Index | [decision records](README.md) |
| Date | 2026-11-28 |
| Issue | [#1577](https://github.com/Hawkynt/Polypiler/issues/1577) |
| Decides | [PRD §4.6](../prd/02-core-architecture.md#s4-6) "inference states", the place of type inference in the [tree IR](0009-structured-tree-ir.md), and how [variants](../reference/variants.md) map to each target |
| Implemented by | epic [#1579](https://github.com/Hawkynt/Polypiler/issues/1579) |

## Context

Perl scalars, PHP and JavaScript variables, Python names, VBScript values, Visual Basic `Variant`s
and Kotlin's `Any` are dynamically typed unless a program proves them otherwise. Typed targets need a
type for every place, and the cheapest correct one is the narrowest type the program proves. Today
five frontends each compute it their own way:

| Frontend | How it types a value | What it does with a value it cannot type |
| --- | --- | --- |
| JavaScript | least fixed point over the assignments and calls of a file, a copy of a function per call site the import asks for (`Inference/TypeInference.cs`); object literals become structs by shape | refuses: `PLY1105` values of two kinds |
| Python | fixed point; two numbers join to the wider, `None` to an optional, empty lists from their appends (`Typing/TypeInference.cs`) | refuses: `PLY1803` |
| Perl | during lowering, no inference pass of its own (`Lowering/PerlLowerer.Types.cs`) | refuses: `PLY1702` and its siblings |
| VBScript | four passes over procedures, class fields and globals inside the lowerer, with hints from use (`VBScriptLowerer.InferProcedureTypes`) | a default: what stays unknown becomes `i32` (`i32[]` for arrays) |
| Classic Visual Basic | flow-sensitive, by lowering a procedure until the kinds stop growing (#1556, `VbaLowerer.Variants.cs`); across the module by call-site clones, at most 4 per procedure and 12 rounds (#1564, `VbaLowerer.Specialisation.cs`) | the IR's `variant` with Visual Basic's helpers |

Five fixed points of one idea, of five strengths; three refuse what they cannot prove, one guesses, and only one falls back to a dynamic value. The older
backlog designed the same engine for a Source Semantic IR with a `dyn` type and `Dyn.*` operations
(#187, #253, #199); [ADR 0009](0009-structured-tree-ir.md) made the tree the only IR and #1556 gave it
the dynamic type it now has, `variant`.

## Decision

1. **One inference framework, in the IR layers, for every dynamically typed source.** A frontend
   types what its own syntax states (literals, declarations, annotations it is told to trust) and
   lowers every other value as a `variant`, every source operation on one as a call of its language's
   semantics helper. The framework then narrows: its fact domain lives in `Polypiler.Core.Analysis`
   (`Dynamic/`), its rewrite is a required pass of `Polypiler.Core.Optimize` (`Specialize/DeDynamicize`)
   that runs before the optional passes, as `PyIntSpecialization` does. No frontend keeps a fixed
   point of its own once it has migrated.

2. **The lattice.** Every place (local, parameter, result, global, field, element) is given one of,
   from most to least static:

   | Level | Element | Example | Becomes |
   | --- | --- | --- | --- |
   | 0 | nothing reaches it | dead code | removed |
   | 1 | **exact scalar**: one IR scalar type, with the range facts of `Range/` | a JavaScript loop counter | `i32` |
   | 2 | **nominal**: one declared class, struct or UDT | a Perl blessed `Point` | the class |
   | 3 | **structural shape**: a record of named fields, each with its own element | `{x: 1, y: 2}`, a Perl hashref with constant keys | a generated record type |
   | 4 | **optional**: one of the above or the language's missing value(s) | `undef`, `None`, `null`/`undefined` | `T?` |
   | 5 | **closed union** of at most 4 members of levels 1 to 4 | a result that is a number or a string | a generated tagged union |
   | 6 | **variant**: anything | a value read from `eval` | the IR's `variant` |

   Joins go up and never down: two exact scalars join to the wider type where the language's
   semantics table says the program cannot tell them apart (a JavaScript `i32` and `f64` are both
   Numbers), else to a union; two shapes with the same field names join field by field, otherwise
   to a union; a union of more than 4 members, a shape whose keys are not constant, a shape that is
   deleted from, enumerated or reached by reflection, is a variant. Each element also carries the
   certainty of PRD §4.6: **Proven** (every path), **Contextual** (a stated contract, checked where
   the value enters), **Guarded** (a speculation with its guard and a valid fallback, #778) or
   **Dynamic**. Only Proven and checked Contextual places are emitted typed without a fallback.

3. **Flow-sensitive within a function, inter-procedural across the module.** Facts are kept per
   program point (decision 3 of ADR 0009); a loop head is iterated until its facts stop growing, as
   VBA's `Heads` are today. Across functions the framework specialises per call-site signature: a
   call whose arguments are all below level 6 calls a clone for that signature. The budgets are
   #1564's: at most 4 clones of one function and 12 rounds over the call graph; beyond them the
   call uses the dynamic function. A function the host can call (exported, `Public`, a module's
   entry point) keeps its declared, dynamic signature and gets clones for the internal calls.
   Exceeding a budget never refuses a program; it only leaves a place dynamic.

4. **Language rules are semantics tables, not framework code.** Each frontend supplies one
   `IDynamicSemantics`: for every operation (arithmetic, comparison, concatenation, truthiness,
   conversion to number and to string, indexing, member get and set, call) and every pair of operand
   elements, the element of the result, whether and how it traps, and the IR the operation expands to
   for that pair (typed operations where the pair allows, else the language's helper). A type test
   of the source (`typeof`, `isinstance`, `VarType`, `ref`, Kotlin `is`) is lowered to a tag test,
   which the framework refines on, so refinement is language-independent. The tables at the start:

   | Language | Coercion the table states |
   | --- | --- |
   | JavaScript | `ToNumber`, `ToString`, `ToPrimitive`; `ToInt32`/`ToUint32` for bit operators; `+` concatenates when either side is a string; `null` and `undefined` distinct |
   | Perl | numeric and string operators are distinct (`+` vs `.`, `==` vs `eq`); a scalar used both ways is a dual value, a shape `{num, str}` only where the program reads both; `undef` is the missing value, 0 or `""` by context |
   | Visual Basic, VBScript | `Integer`→`Long`→`Double` promotion on overflow, `Null` propagates, `Empty` is 0 or `""` by context, Currency and Date as in #1556 |
   | Python | no implicit coercion: `int + str` traps with `TypeError`; `bool` is an `int`; unbounded `int` through `PyIntSpecialization` |
   | Kotlin `Any`, Lua, PHP | Kotlin: no coercion, smart casts are refinements; Lua: string↔number in arithmetic, `..` concatenates; PHP: its numeric-string rules — each added with its frontend |

5. **Structural shapes become types.** An object, hash or dict built with constant keys, whose keys
   are never deleted, enumerated or computed, and that does not escape into a reflective operation,
   is a shape. Shapes reaching one place with the same field names are one generated type, named
   after the language's own name where it has one, else after the place (`<Function><Variable>Shape`).
   A shape keeps its source's identity semantics: a JavaScript object or a Perl hashref is a
   reference type, a Visual Basic UDT a value type.

6. **The backends map the lattice to the target's own types, and the dynamic type to `object` plus
   exact helpers.** The C# emitter writes `dynamic` only where the C# runtime binder's semantics are
   the source's: late-bound calls on COM/Automation objects (`CreateObject`, `GetObject` from Visual
   Basic and VBScript), whose `IDispatch` binding the binder implements, on the profiles with the
   DLR in the framework (`net4.6`, `net4.8`, `netcoreapp3.1`, `net8.0`, `net10.0`); `net2.0` and
   `net3.5`, which have no DLR, and the `netstandard` profiles, whose `dynamic` would need the
   `Microsoft.CSharp` package, use `Type.InvokeMember`. It never writes `dynamic`
   for arithmetic, comparison, conversion or concatenation of source values: the binder applies C#'s
   rules (wrapping `int` addition, C# string concatenation, no `ToNumber`, no promotion to `Long`), so
   `dynamic` would compile and silently compute something else.

   | Target | Exact, nominal | Shape | Optional | Closed union | Variant |
   | --- | --- | --- | --- | --- | --- |
   | C# | the type | `sealed class` (reference source) or `struct` (value source) | `T?` | a generated `readonly struct` of tag and payloads, its accessors throwing `InvalidCastException` | `object` + the helpers of #1556; `dynamic` only for COM late binding as above |
   | JavaScript | native value | object | `null`/`undefined` | native values where `typeof` tells the members apart, else the tagged pair | a JavaScript source's own value; the tagged pair where the source has tags JavaScript cannot tell apart (#1556) |
   | TypeScript | the type | `interface` + object | `T \| undefined` | `A \| B` | `unknown` + helpers |
   | Python | native value, hinted | `@dataclass(slots=True)` | `T \| None` | native values, hint `A \| B` | the tagged tuple where tags matter (#1556) |
   | Rust | the type | `struct` | `Option<T>` | `enum` with payloads | a generated `enum` of every tag, never `dyn Any` |
   | Go | the type | `struct` | pointer or `ok` pair | `struct` of tag and payloads when all members are scalars, else a sealed interface | `any` + helpers with type switches |
   | Java / Kotlin | the type | `record` / `data class` | nullable reference | sealed interface + records (Java 17 on), abstract class before; Kotlin `sealed class` | `Object`/`Any?` + helpers |
   | C++ | the type | `struct` | `std::optional` | `std::variant` (C++17), tagged struct before | a generated class of tag and payload |
   | Visual Basic | the type | `Type` (classic), `Class` (VB.NET) | `Variant`/`Nullable` | `Variant` (classic), `Object` + helpers (VB.NET, `Option Strict On`) | `Variant` / `Object` + helpers |

7. **How static a translation is, is reported.** Every translation counts the places of each level
   and certainty, per function and per module, and names for every place that stayed a variant the
   first join that made it one ("assigned a String at line 12 and a Long at line 9"). The static
   ratio (places below level 6 over all places) is a column of the translation showcase.

## What becomes of the existing work

- **Visual Basic migrates first**: it has the oracle (ADR 2600) and the most complete inference.
  `VbaLowerer.Variants.cs`'s fixed point and `VbaLowerer.Specialisation.cs` are replaced by the
  framework with identical results: the #1556 and #1564 tests, the oracle rows and the showcase do
  not change. VBScript follows, then JavaScript (its `PLY1105` refusals become variants), Python
  (`PLY1803`) and Perl.
- **#187** (type and shape sets) is the lattice of decision 2; **#253** (the engine with its four
  states) is decisions 1 and 3; both are re-scoped to the tree IR by the issues of epic #1579.
  **#199** (`dyn` and `Dyn.*` over a Source Semantic IR) is superseded: the IR's `variant` and the
  semantics helpers are the dynamic layer, and ADR 0009 rules out a second IR form. **#776** stays
  the umbrella of the JavaScript, Python and Perl language rules; **#778** (guards), **#492**
  (specialization budget) and **#777** (`--assume`) are the Guarded and Contextual states.
- The planned language specialisations keep their IDs and become rows of their language's
  semantics table over the framework: O0060 (#255) and O0061 (#254) for JavaScript, O0063 (#780) for
  Python, O0065 (#779) for Perl. O0062, `PyIntSpecialization`, stays the pass it is.

## Decisions made in the implementation

Where this record is silent, the implementation of epic #1579 decided:

- **Canonical elements.** An optional is the outermost element: a union's members are of levels 1 to
  3, and its missing values those of the optional around it; an optional of a union is of level 5.
  An exact element carries the language's kind where two kinds share one IR type (Visual Basic's
  `Currency` on an `i64`, `Date` on an `f64`). Shapes nested deeper than four widen to a variant.
- **Unions in the IR.** The members of `union<…>` are kept in the ordinal order of the names the IR
  writes them with, and a member is named by its index in that order; the tag of a union is that
  index, never a COM number.
- **The contract of a table.** `Expand` returns no expansion where the language's helper stays; a
  unary operation passes `Nothing` as the second element; Visual Basic's `Eqv` and `Imp` are operations
  of their own. A table also recognises the calls of its helpers (`Classify`), names its helpers
  (`IsHelper`) and its kinds (`Name`, which names clones: `Add_Long_Long`).
- **Typing a local.** A local is typed when every read of it sees one exact kind; an assignment of
  another value reaches no read, so it is a dead store that keeps only its effects. A local no read
  sees has the one kind of everything assigned it but the language's initial value.
- **Range facts** (#1591). An exact integer element carries a range where it says more than the type;
  ranges join to the hull, meet to the intersection, and the widening at a loop's head drops one that
  grew. The test of a counted loop (`DynamicOperation.LoopTest`) bounds the counter by the limit in
  the direction of the step, and the Visual Basic table keeps `+ - *` of two integers of one kind each
  in the kind they start in where the result's range fits it. So a `Variant` `For` counter over
  constant bounds is proven of its start's kind; a `Long` accumulator still stays a variant, as its
  range grows with every pass and is widened away - counting the passes is not done.
- **A frontend may write the typing** (#1591). `DeDynamicize.Typing` gives, without rewriting the
  module, what the analysis found for every function and clone: the typed locals, what each local holds
  anywhere and at each loop's head, where each call goes, the result. Visual Basic lowers its module
  dynamic, analyses it and lowers it again with that typing, so that every typed operation is its own
  lowering of the type and the output equals that of its former inference byte for byte; the
  rewriting pass (`DeDynamicize.Run`) stays for the frontends that leave the typing to the pipeline.
- **Refinement** follows tests of the tag of a local, through conversions, `And`, `Or`, `Not` and a
  switch on the tag; a test of a copy of the tag (Visual Basic's `Select Case VarType(v)`) is not
  followed yet.
- **The report** counts the places of the emitted module by the type each was given, the helpers of the
  language aside; a path it cannot write is the existing `PLY0005` with its exit code, as ADR 0003 has
  no code 74. The showcase shows the ratio per example.
- **Visual Basic's migration** (#1591) is done: `VisualBasicInference.Framework`, the default, is the
  framework's typing written by the frontend, whose fixed points within a procedure (#1556) and across
  the module (#1564) are gone; `VisualBasicInference.Dynamic` leaves every `Variant` to the pipeline's
  rewrite. The translations of every classic fixture are byte for byte those of the former inference
  (`tests/fixtures/visualbasic/classic/golden`), but for the `ByVal` parameter the body assigns, which
  that inference typed by its calls.

## Rejected options

- **Keep one inference per frontend.** Five fixed points of five strengths, a sixth for every new
  dynamic language, and a refusal or a guess instead of a fallback in four of them.
- **C# `dynamic` for every variant.** The runtime binder computes C#'s semantics, not JavaScript's,
  Perl's or Visual Basic's; it needs the DLR, which `net2.0` and `net3.5` lack; and a binder call
  site costs more than the type switch of a helper.
- **Unification (Hindley-Milner).** Dynamic programs reassign a variable with another type and
  branch on types; unification has neither subtyping, nor unions, nor flow sensitivity, and answers
  such a program with a type error or a wrong type.
- **An SSA storage form for the analysis.** Ruled out by ADR 0009; its decision 4 allows a derived
  SSA view inside the analysis if a sparse formulation is ever measured to be needed.

## Follow-up issues

In the order they can be taken (epic [#1579](https://github.com/Hawkynt/Polypiler/issues/1579)):

- [#1581](https://github.com/Hawkynt/Polypiler/issues/1581) analysis: the type lattice of dynamic places — exact, nominal, shape, optional, closed union, variant — with certainty states — ready
- [#1582](https://github.com/Hawkynt/Polypiler/issues/1582) ir: closed tagged unions of proven types — ready
- [#1583](https://github.com/Hawkynt/Polypiler/issues/1583) ir: variant tags for object references, sequences, maps, functions, decimals and a second missing value — ready
- [#1584](https://github.com/Hawkynt/Polypiler/issues/1584) analysis: semantics tables — the IDynamicSemantics contract and the Visual Basic table — after #1581
- [#1585](https://github.com/Hawkynt/Polypiler/issues/1585) analysis: de-dynamicize variant places within a function, flow-sensitively — after #1581, #1584
- [#1586](https://github.com/Hawkynt/Polypiler/issues/1586) analysis: refine variant facts on tag tests in each branch — after #1585
- [#1587](https://github.com/Hawkynt/Polypiler/issues/1587) analysis: specialise functions per call-site signature across the module — after #1585
- [#1588](https://github.com/Hawkynt/Polypiler/issues/1588) analysis: structural shapes of object, hash and dict values become generated records — after #1585, #1583
- [#1589](https://github.com/Hawkynt/Polypiler/issues/1589) analysis: small sets of proven types become closed unions — after #1585, #1582
- [#1590](https://github.com/Hawkynt/Polypiler/issues/1590) cli: report how much of a translation is statically typed — after #1585
- [#1591](https://github.com/Hawkynt/Polypiler/issues/1591) frontend(visualbasic): move Variant inference onto the shared framework — after #1587, #1586
- [#1592](https://github.com/Hawkynt/Polypiler/issues/1592) frontend(vbscript): VBScript values as variants narrowed by the shared framework — after #1591
- [#1593](https://github.com/Hawkynt/Polypiler/issues/1593) frontend(javascript): values of two kinds become variants with JavaScript's semantics table instead of PLY1105 — after #1587, #1588, #1586
- [#1594](https://github.com/Hawkynt/Polypiler/issues/1594) frontend(python): names of several types become variants with Python's semantics table instead of PLY1803 — after #1587, #1586
- [#1595](https://github.com/Hawkynt/Polypiler/issues/1595) frontend(perl): scalars as variants with Perl's semantics table — numeric and string operators, dual values, undef — after #1587, #1586
- [#1596](https://github.com/Hawkynt/Polypiler/issues/1596) backend(csharp): closed unions as tagged structs and shapes as classes — after #1582, #1588
- [#1597](https://github.com/Hawkynt/Polypiler/issues/1597) backend(csharp): late-bound calls on COM objects as dynamic where the framework has the DLR, Type.InvokeMember elsewhere — after #1583, #642
- [#1598](https://github.com/Hawkynt/Polypiler/issues/1598) backend(javascript): closed unions as native values or tagged pairs and shapes as objects — after #1582, #1588
- [#1599](https://github.com/Hawkynt/Polypiler/issues/1599) backend(python): closed unions as native values with hints and shapes as slotted dataclasses — after #1582, #1588
- [#1600](https://github.com/Hawkynt/Polypiler/issues/1600) backend(rust): closed unions as enums and shapes as structs — after #1582, #1588
- [#1601](https://github.com/Hawkynt/Polypiler/issues/1601) backend(go): closed unions as tagged structs or sealed interfaces and shapes as structs — after #1582, #1588
- [#1602](https://github.com/Hawkynt/Polypiler/issues/1602) backend(jvm): closed unions as sealed interfaces with records and shapes as classes — after #1582, #1588
- [#1603](https://github.com/Hawkynt/Polypiler/issues/1603) backend(cpp): closed unions as std::variant and shapes as structs — after #1582, #1588
- [#1604](https://github.com/Hawkynt/Polypiler/issues/1604) backend(visualbasic): closed unions as Variant or Object and shapes as Type or Class — after #1582, #1588

Related: [variants](../reference/variants.md) · [IR and analysis contracts](../architecture/IR.md) ·
[ADR 1000](1000-grammar-described-frontends.md), the other half of the frontend direction.
