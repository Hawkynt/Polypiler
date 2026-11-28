# ADR 1000 — Grammar-described frontends

Area: languages

| | |
| --- | --- |
| Status | accepted |
| Index | [decision records](README.md) |
| Date | 2026-11-28 |
| Issue | [#1578](https://github.com/Hawkynt/Polypiler/issues/1578) |
| Evidence | [`evidence/1000`](evidence/1000): the spike's generator, grammar, harnesses and recorded output |
| Implemented by | epic [#1580](https://github.com/Hawkynt/Polypiler/issues/1580) |

## Question

Should the frontends' lexers and parsers be generated from grammar descriptions, so that a new
language version or dialect is a grammar change, the parser stays reviewable against its
specification and the frontend works on a typed tree as early as possible? With which generator, and
what becomes of the parsers written by hand?

## What exists

Two frontends use the reference compiler's own parser: C# and VB.NET (Roslyn, MIT). JavaScript uses
Acornima (BSD-3-Clause, a port of acorn). Every other frontend has its own, and the best of them
are conversions of the reference implementation, rule by rule, measured against it:

| Frontend | Parser | Lines | Measured against |
| --- | --- | --- | --- |
| Python | `Syntax/PyLexer.cs`, `PyParser*.cs`: CPython's pegen parser converted from `Grammar/python.gram` | ~7,000 | CPython: 1870 files × 7 versions and 84068 snippets, 0 disagreements (#1560) |
| Go | `Syntax/GoScanner.cs`, `GoParser.cs`: `go/scanner` and `go/parser` converted | 2,270 | `go/parser`: 11494 files, 0 disagreements (#1557) |
| Perl | `Syntax/PerlLexer.cs`, `PerlParser*.cs` | 3,700 | perl (#1563) |
| Rust | `Syntax/Lexer.cs`, `Parser.cs` | 2,370 | — |
| Classic Visual Basic | `Parsing/VbaParser.cs` | 1,170 | LibreOffice Basic (ADR 2600) |
| VBScript | `Parsing/VBScriptLexer.cs`, `VBScriptParser.cs` | 1,190 | — |
| C++ | `Syntax/CppTokens.cs`, `CppParser.cs` (a subset) | 1,540 | — |
| Batch | `BatchFrontend.Parser.cs` | 640 | the DOS command oracle |

A hand conversion is exact but has to be redone by hand for each new language version, and every
dialect is an `if` in the code rather than a difference between two grammars.

## Grammar descriptions by language

The licence decides the rung of the sourcing ladder (AGENTS.md): rung 1 may be converted, rung 2
only read as a specification; constants and keyword tables are copied on every rung.

| Language | Normative grammar | Other grammar descriptions (licence) | Rung of the best one |
| --- | --- | --- | --- |
| C# | ECMA-334 grammar; Roslyn (MIT) | grammars-v4 `csharp/v7` (MIT), `v6` (EPL-1.0), `v8-spec` (no notice) | keep Roslyn |
| VB.NET | Roslyn (MIT) | — | keep Roslyn |
| JavaScript | ECMA-262 grammar | grammars-v4 `javascript` (MIT), acorn (MIT) | keep Acornima |
| TypeScript | none (the specification is retired); `parser.ts` of the compiler (Apache-2.0) | grammars-v4 `typescript` (MIT), tree-sitter-typescript (MIT) | 1 |
| Python | `Grammar/python.gram`, PEG, the parser CPython is generated from (PSF-2.0) | grammars-v4 `python3_14` (MIT) | 1, normative |
| Rust | the Reference's grammar notation (MIT/Apache-2.0) | grammars-v4 `rust` (MIT), tree-sitter-rust (MIT) | 1 |
| Go | the specification's EBNF (code BSD-3-Clause); `go/parser` (BSD-3-Clause) | grammars-v4 `golang` (BSD) | 1, normative |
| Kotlin | the ANTLR grammar of the Kotlin specification (Apache-2.0) | grammars-v4 `kotlin` (derived from it) | 1, normative |
| Perl | none: `perly.y` and `toke.c` are the language (GPL-1.0-or-later or Artistic-1.0) | tree-sitter-perl (MIT); grammars-v4 has none | 2 for perl, 1 for tree-sitter-perl |
| PHP | `zend_language_parser.y` (PHP-3.01) | grammars-v4 `php` (MIT), tree-sitter-php (MIT) | 1 (MIT grammars), 2 for php-src |
| VBA, VB6 | [MS-VBAL] grammar in ABNF (Open Specification Promise) | grammars-v4 `vba/vba7_1` (MIT), `vb6` (MIT), `vba/vba6` (GPL-3.0, rung 2) | 3, normative |
| VBScript | none published; Microsoft's language reference | — | 3 (an overlay of the VB grammar) |
| Batch | none: `cmd.exe` parses in phases (percent expansion, delayed expansion, special characters), not by a context-free grammar | — | keep the hand parser |
| C | ISO C Annex A | grammars-v4 `c` (BSD) | 3 |
| C++ | ISO C++ Annex A, ambiguous without name lookup | grammars-v4 `cpp` 14/20/23 (MIT) | keep the hand subset; decided again with a full C++ frontend |
| Java (source) | JLS chapter 19 | grammars-v4 `java/java20` (MIT) | 3 |
| Lua | the manual's §9, complete syntax in EBNF (MIT) | grammars-v4 `lua` (MIT) | 1, normative |

grammars-v4 was read at `antlr/grammars-v4@5ed230f528feb62b45e17880d9fb0af8ca2ef7c9`; every licence is
the notice at the head of the grammar file, as grammars-v4 has no licence of its own.

## Options

| Option | Generator | Tree | Dialects | Exact verdicts and messages | Verdict |
| --- | --- | --- | --- | --- | --- |
| 1. ANTLR 4, C# target | Java tool at development time, generated code committed; runtime `Antlr4.Runtime.Standard` (BSD-3-Clause) | an untyped parse tree, a visitor per language to our tree | one grammar file per dialect, or predicates | ANTLR's own messages, adaptive LL(\*) decides differently from the reference parser | rejected: measured below |
| 2. Our own PEG generator | `polypiler-tooling grammar generate`, C#, generated code committed | typed nodes declared in the grammar | overlays per version and dialect | ordered choice reproduces a recursive-descent reference; a second pass with `invalid_*` rules gives its messages, as pegen does | **chosen** |
| 3. tree-sitter | grammar in JavaScript, tables and external scanners in C | an untyped concrete tree | one grammar per dialect | built for editors: it recovers and accepts more than the compiler | rejected: a native runtime per platform, no compiler verdicts |
| 4. Keep converting by hand | — | ours | `if`s in the code | exact | rejected as the default; kept where no grammar exists |

## Spike: the Go grammar, generated both ways

Both parsers were generated and run on every `.go` file of the pinned Go distribution's `src/`
(go1.27.4, 8088 files, 88.4 MiB), against the hand-written parser (`GoParser.ParseFile`), in one
process, Release build, .NET 10, after a cold round and three warm rounds:

| Parser | Source | Generated | Warm time | Throughput | Against the hand parser | Verdicts that differ |
| --- | --- | --- | --- | --- | --- | --- |
| hand-written (`go/parser` converted) | — | — | 4.0–5.2 s | 17–22 MiB/s | 1× | — |
| ANTLR 4.13.2, grammars-v4 `golang` | `GoLexer.g4`, `GoParser.g4`, `GoParserBase.cs` | 7,560 lines | 103–112 s (cold 377 s) | 0.8–0.9 MiB/s | **22–28× slower** (77× cold) | **44**: 41 files it refuses (among them `builtin/builtin.go`, `fmt/example_test.go`), 3 it accepts |
| PEG generator of the spike | `go.pgram`, 95 lines from the Go specification, over the existing scanner | 1,870 lines | 7.9–8.3 s | 10.7–11.2 MiB/s | **1.5–1.8× slower** | **5**, all in `testdata/` |

ANTLR's two-stage strategy (SLL, then full LL) fell back to LL on 4650 of the 8088 files: the
grammar's semantic predicates defeat SLL. The largest file, `ssa/opGen.go` (3.8 MiB), took 2.0 s
against 0.27 s by hand. Of the five files the PEG parser accepts and `go/parser` refuses, four fail
checks `go/parser` makes on a parsed construct (more than two `range` variables, mixed named and
unnamed parameters, a `go` or `defer` of something that is not a call); they belong to a post-parse
check, as `PyCompileCheck` is Python's. The fifth (`expected type, found 1`) is the spike grammar
taking an expression where a type argument must be a type. Two first-run
differences were the grammar's (`const _ int` and a unary `~`, which `go/parser` accepts and
`go/types` refuses) and were fixed in the grammar, which is the point: the fix was one line of
grammar each. The PEG parser builds a generic tree and the hand parser `go/ast` nodes; it allocated
4.97 GiB against 4.56 GiB, untuned (three memoized rules, a copied child array per node). Commands and
the full output are in [`evidence/1000`](evidence/1000).

## Decision

1. **Frontends are generated from grammar descriptions by our own PEG generator** wherever a
   grammar exists. The generator is `polypiler-tooling grammar generate`; the runtime it targets
   (token buffer, memo tables, positions, error collection) is a new project `Polypiler.Core.Syntax`
   in layer 1, which frontends reference. The generated C# is committed beside the grammar, so
   diffs are reviewed, debugged and counted by the coverage gate like any other code; the CI job
   `grammar drift` regenerates every grammar and fails on any difference.
2. **The grammar format is pegen's**, so CPython's `python.gram` is read as it is: ordered choice,
   lookahead (`&`, `!`), cuts (`~`), forced tokens (`&&`), gathers (`s.e+`), left recursion by seed
   growing, memoization per rule (`(memo)`), and named items with typed actions that build the
   frontend's own nodes (`if_stmt[IfStmt]: 'if' c=named_expression ':' b=block { new IfStmt(c, b) }`).
   Two things are added: **hooks**, named parser state a hand-written partial class supplies (the
   spike's `nolit(…)`, `lit(…)` and `?litok` carry Go's composite-literal rule of control clauses),
   and **overlays**: a grammar `@extends` another and replaces, adds or removes rules or
   alternatives, and an alternative may be guarded by the profile (`@version >= 3.10`,
   `@dialect vbscript`), which is how pegen's `feature_version` checks, Visual Basic's dialects and
   C# language versions are written.
3. **Lexers stay hand-written token sources where the language needs them.** Python's
   `INDENT`/`DEDENT` and line joining, Go's semicolon insertion and Perl's context-dependent lexing
   (`/` as division or a pattern, here-documents, quote-like operators, prototypes that change the
   parse) are not regular, and the scanners that do them are already exact. A grammar may declare
   its tokens as patterns for a language whose lexer is regular (Lua, VBScript). Perl therefore cannot
   be described by a grammar alone: its hand lexer stays, driven by parser hooks, and only the
   statement and expression layer above it is generated.
4. **Errors come in two passes, as in pegen.** The first pass is the fast one. On failure the parser
   runs again with the `invalid_*` rules enabled, which produce the reference compiler's message for
   the error; what neither pass explains is reported at the farthest token reached. Recovery after the
   first error is not needed: every frontend's verdict is decided by its first error (#1557, #1560).
5. **Performance gate.** A generated parser may take at most **2×** the time of the parser it
   replaces on that language's oracle corpus (the spike's untuned 1.5–1.8×), measured by
   `polypiler-tooling bench`; above it the migration does not merge.
6. **What is kept.** Roslyn for C# and VB.NET: it is the reference compiler. Acornima for JavaScript:
   a maintained port of acorn whose verdicts are the reference's and which covers every ECMAScript
   version the profiles name. The Batch parser: `cmd.exe` has no grammar. The C++ subset parser,
   until a full C++ frontend decides between a grammar with name-lookup hooks and a library.
7. **What migrates, in this order**, each in its own pull request that deletes the hand parser it
   replaces and keeps the language's oracle comparison at its current count of disagreements (0
   for Python and Go), the trees identical and the showcase unchanged:
   1. Python, from `python.gram` itself, every version 3.8 to 3.14 as overlays: the existing parser
      is already its hand-made output, and each CPython release becomes a grammar update.
   2. Classic Visual Basic from the [MS-VBAL] grammar, the profiles `vb6`, `vba6`, `vba7` and
      `vba7-64` as overlays; then VBScript as an overlay of the same grammar.
   3. Go, from the specification's EBNF, with the post-parse checks of `go/parser` in a check pass.
   4. Rust, from the Reference's grammar.
   5. Perl's statements and expressions, over the hand lexer.
   New languages — Lua, Kotlin, TypeScript, PHP, Java source, Dart — start on the generator.

## Consequences

- A frontend's parser is a grammar, a token source and a hooks class; a dialect is an overlay.
- The generator and the runtime are code of ours with the coverage rules of every other project;
  the generated parsers are covered by each frontend's syntax tests.
- ANTLR, tree-sitter and grammars-v4 remain reading material: a grammars-v4 grammar under MIT or BSD
  may be converted into our format (rung 1), the others are specifications (rung 2).

## Follow-up issues

In the order they can be taken (epic [#1580](https://github.com/Hawkynt/Polypiler/issues/1580)):

- [#1605](https://github.com/Hawkynt/Polypiler/issues/1605) infra: Polypiler.Core.Syntax — the runtime of generated parsers — ready
- [#1606](https://github.com/Hawkynt/Polypiler/issues/1606) infra: polypiler-tooling grammar generate — pegen-format grammars to C# parsers with typed actions — after #1605
- [#1607](https://github.com/Hawkynt/Polypiler/issues/1607) infra: grammar drift CI job — regenerate every grammar and fail on any difference — after #1606
- [#1608](https://github.com/Hawkynt/Polypiler/issues/1608) infra: grammar overlays — @extends, rule replacement and version or dialect guards — after #1606
- [#1609](https://github.com/Hawkynt/Polypiler/issues/1609) infra: the invalid-rule second pass and farthest-token errors of generated parsers — after #1606
- [#1610](https://github.com/Hawkynt/Polypiler/issues/1610) infra: parser benchmarks — generated against hand-written parsers on each oracle corpus — after #1606
- [#1611](https://github.com/Hawkynt/Polypiler/issues/1611) frontend(python): generate the Python parser from CPython's python.gram — after #1607, #1608, #1609, #1610
- [#1612](https://github.com/Hawkynt/Polypiler/issues/1612) frontend(visualbasic): generate the classic Visual Basic parser from the MS-VBAL grammar with profile overlays — after #1607, #1608, #1609, #1610
- [#1613](https://github.com/Hawkynt/Polypiler/issues/1613) frontend(vbscript): VBScript as an overlay of the Visual Basic grammar — after #1612
- [#1614](https://github.com/Hawkynt/Polypiler/issues/1614) frontend(go): generate the Go parser from the specification's grammar, with go/parser's post-parse checks in a check pass — after #1607, #1608, #1609, #1610
- [#1615](https://github.com/Hawkynt/Polypiler/issues/1615) frontend(rust): generate the Rust parser from the Reference's grammar — after #1607, #1608, #1609, #1610
- [#1616](https://github.com/Hawkynt/Polypiler/issues/1616) frontend(perl): generate Perl's statement and expression layer over the hand lexer — after #1607, #1608, #1609, #1610, #1611

Related: [ADR 0300](0300-static-types-first.md), the other half of the frontend direction ·
[the Python frontend](../architecture/PYTHON-FRONTEND.md) · [the Go frontend](../architecture/GO-FRONTEND.md) ·
[the classic Visual Basic frontend](../architecture/VISUALBASIC-FRONTEND.md).
