# ADR 0004 — Command-line grammar, option naming and output conventions

Area: foundation

**Status:** accepted · **Date:** 2026-11-13 · **Issue:** [#16](https://github.com/Hawkynt/Polypiler/issues/16) (`ADR-CLI`) · **Index:** [decision records](README.md)

**Decides:** [PRD §8 P0](../prd/04-roadmap-and-delivery.md#s8) "CLI design", and what
[§10](../prd/04-roadmap-and-delivery.md#s10) leaves open, for the parsing foundation
(`CLI-FOUNDATION`, [#25](https://github.com/Hawkynt/Polypiler/issues/25)) and every command of the CLI
epic (`CLI-SUITE`, [#879](https://github.com/Hawkynt/Polypiler/issues/879)).

Issue names in `code font` are the backlog identifiers of the issues that implement or consume this
decision.

## Context

[PRD §10](../prd/04-roadmap-and-delivery.md#s10) fixes the executable name `polypiler` and
eighteen example invocations; [§10.1](../prd/04-roadmap-and-delivery.md#s10-1) promises stable exit
codes and structured diagnostics; [§10.2](../prd/04-roadmap-and-delivery.md#s10-2) requires every
manifest to record the "requested CLI/config". None of them says which options are global, how a list
is written, what goes to which stream, how an identifier is resolved or how a command line is
normalised before it is recorded. An option name changed after the first release breaks every script
and every recorded manifest, so the rules are fixed here, before the first command exists.

## Decision

| # | Question | Decision |
| --- | --- | --- |
| 1 | Parsing library | **System.CommandLine 2.0.x** (MIT) behind the existing `Program.Run(args, output, error)` seam. The grammar below is the contract, not the library's defaults: the foundation closes four library behaviours the grammar refuses and never shows or matches the library's own messages. |
| 2 | Global or per command | Two **global** options, `--diagnostics` and `--quiet`, accepted before or after the command name. Every other option, including `--opt`, `--disable-opt`, `--assume` and `--strict`, belongs to the commands that declare it and is accepted **only after** the command name. |
| 3 | Multi-valued options | A **list** option takes a comma list and may be repeated; both spellings mean the same. An option whose values can contain a comma (`--assume`) is **repeat-only**. |
| 4 | Output channels | The **result on stdout, every diagnostic on stderr**. A command that produces or checks an artefact ends stdout with one summary line; a command whose result is a report offers `--format text\|json` for stdout. `--diagnostics text\|json` selects the stderr format and nothing else. |
| 5 | Identifiers | **Exact identifiers only.** No aliases, no case folding, no inference of `--from` from a file extension. |
| 6 | Configuration | **None** in the first release: no configuration file, no response file, no environment variable that changes what a command line means. |

## Grammar

The grammar is defined over the argument vector. Every terminal of the first block is one whole argument
as the operating system delivers it; splitting and quoting are the shell's business.

```ebnf
invocation = help | version | run ;

help       = (* no argument at all *)
           | "help" , [ word ]
           | { any } , ( "--help" | "-h" ) , { any } ;          (* before the first "--" *)
version    = "--version" ;                                       (* the only argument *)

run        = { global } , command , { global | option | operand } , [ "--" , { any } ] ;
global     = option ;                 (* restricted to --diagnostics and --quiet *)
option     = flag
           | attached
           | separate , plain ;
flag       = name ;                   (* an option declared without a value *)
attached   = name , "=" , { char } ;  (* one argument: --opt=speed; the value may be empty *)
separate   = name ;                   (* an option declared with a value: --opt speed *)
operand    = plain ;
plain      = "-" | ( char - "-" ) , { char } | (* the empty argument *) ;
any        = { char } ;

command    = word ;
name       = "--" , word ;
word       = lower , { lower | digit } , { "-" , ( lower | digit ) , { lower | digit } } ;
lower      = "a" | "b" | (* ... *) | "z" ;
digit      = "0" | "1" | (* ... *) | "9" ;

(* Inside the value of a list option *)
list       = item , { "," , item } ;
item       = ( char - "," ) , { char - "," } ;
```

The rules the notation cannot carry:

1. **Option-like arguments.** Before the first `--`, an argument of two or more characters that starts
   with `-` is an option and nothing else. It is never an operand and never the separate value of the
   option before it. A lone `-` and the empty argument are plain.
2. **Names are exact.** Matching is ordinal and case-sensitive. There are no abbreviations (`--out`),
   no short options other than `-h`, no bundling, no `/x` spelling and no `--name:value`.
3. **Two value spellings.** `--name value` and `--name=value`. A value that starts with `-` must be
   attached (`--limit=-1`). `--name=` is the empty value, the same as `--name ""`; whether an empty
   value is acceptable is the option's business.
4. **No optional values.** An option always takes a value or never does. A flag given a value
   (`--force=true`) is refused. Behaviour that is on by default is switched off by a flag named
   `--no-<name>`; where the positive flag exists as well, the two are declared mutually exclusive.
5. **Arity.** An option that is not multi-valued appears at most once, flags included.
6. **`--` ends option parsing.** Everything after it is an operand, whatever it looks like.
7. **`@` has no meaning.** `@file` is an ordinary operand; no response file is read.
8. **Position.** The command name is the first argument that is neither an option nor a global
   option's value. Global options may stand before or after it; every other option and every operand
   stands after it. Beyond that, order carries no meaning, except among the items of a list option
   declared as a sequence.
9. **Help and version come first.** No argument at all prints the root help. `help` alone does the
   same; `help <word>` is `<word> --help`, where the word is a command or a help topic (commands and
   topics share one namespace; `help` itself is reserved); `help` takes at most one word. If `--help`
   or `-h` stands anywhere before the first `--`, the help of the command on that line is printed and
   nothing else on the line is checked; without a command it is the root help; with an unknown one it
   is `PLY0002`. `--version` is recognised only as the sole argument.
10. **One refusal.** A command line that breaks the grammar is refused with exactly one diagnostic —
    the first violation found — exit code 2, an empty stdout, and nothing executed: no input is read
    and no output directory is created.

A refusal is `PLY0002` for an unknown command and `PLY0003` for everything else: unknown option
(wrong case, abbreviation, short option, `--name:value`, a command option before the command name, an
option of another command), option without its value, flag given a value, option given more than once,
value outside the allowed set, empty list item, missing required option, missing or surplus operand,
no command. Options a command declares exclusive are `PLY0011`. The numbers and the two renderings
belong to ADR 0003 (diagnostics and exit codes, [#17](https://github.com/Hawkynt/Polypiler/issues/17));
the message texts belong to the foundation issue.

## Options

**Scope.** Three groups, by who declares the option:

| Group | Options | Accepted | Declared by |
| --- | --- | --- | --- |
| Global | `--diagnostics text\|json` (default `text`), `--quiet` | on every command, before or after its name | the foundation |
| Command | the command's own, e.g. `--from`, `--to`, `--output`, `--show`; and the four pipeline options `--opt`, `--disable-opt`, `--assume`, `--strict` on the commands that run the pipeline | after the command name, on the commands that declare them | the command; the pipeline options once, attached by `translate`, `inspect`, `generate` and `recover` |
| Contributed | options another area adds to a command, e.g. `--cpu`, `--strategy` | after the command name, on the commands they were registered for | the owning area, through the option registry ([#178](https://github.com/Hawkynt/Polypiler/issues/178)) |

A global option is one that changes how every command reports and never what it produces. `--opt`
before the command name would have to be accepted by `search` and `capabilities`, where it means
nothing; there it would be either silently ignored or refused depending on what follows, and an
option that is accepted and ignored is the failure the registry exists to prevent.

**Kinds.** Every option is declared as exactly one of:

| Kind | Written | Example |
| --- | --- | --- |
| flag | `--name` | `--verify`, `--strict` |
| single | `--name value` or `--name=value`, once | `--opt speed` |
| list | comma list, repeatable, items concatenated in the order written | `--show types,ranges`, `--export A --export B` |
| repeat-only | one value per occurrence, never split | `--assume "Sum.n in [0, 31]"` |

A list is declared either a **set** (duplicates collapse, order carries no meaning: `--disable-opt`,
`--show`, `--export`) or a **sequence** (order kept). An empty item — `a,,b`, a trailing comma, an
empty value — is refused.

**Naming.** `--` followed by lower-case words joined by single hyphens, as the `word` production
states. One name means one thing on every command that has it: `--from` and `--to` are always
identifiers, `--output` is always where the result goes (a directory for a command that produces an
artefact, a file for a command that prints a report), `--format` is always the stdout format.

## Identifiers

Languages, formats and CPUs (`--from`, `--to`, `--cpu`), profiles (`--source-profile`,
`--target-profile`) and every other value that names a registered thing are matched **exactly**:
ordinal, case-sensitive, against the identifier as its registry spells it — the folder names of
`spec/languages/` and `spec/targets/`, plus the identifiers other areas contribute through the option
registry (`bitmapping`, formats, CPUs). `csharp` is accepted; `cs`, `c#` and `CSharp` are not.

`--from` is never inferred. `.bas` is PowerBASIC, FreeBASIC, Visual Basic or tokenized BASIC; `.dll`
is MSIL or native x86; `.json` is data or a bit-mapping document; `.asm` and `.bin` name no CPU at
all. A guess that is right for most files is silently wrong for the rest.

A missing profile takes the `defaultProfile` of the language's document. An identifier that no
registry knows is `PLY0006` (exit 2). A value outside a set that the command itself fixes — an
enumeration such as `--opt`, or the languages of a capability report or a catalogue — is `PLY0003`
with the allowed values (exit 2).

## Output channels

| Stream | Carries |
| --- | --- |
| stdout | The result of the command and nothing else: help and version text; the report of `inspect`, `capabilities` and `search`; the per-item lines and the summary line of a command that produces or checks an artefact. |
| stderr | Every diagnostic — info, warning, error — in the format `--diagnostics` selects, in emission order; then, in text format, the tally line. |

- **A command that produces or checks an artefact ends stdout with one summary line:**
  `<output>: <state>` for `translate`, `generate`, `recover` and `verify`, the package line for
  `package`. A script reads the last line and the exit code.
- **A report command prints its report** (`inspect`, `capabilities`, `search`): as text, or with
  `--format json` as exactly one JSON document and nothing else. A command without `--format` has no
  JSON result.
- **`--diagnostics` and `--format` are independent.** The first selects the stderr format, the second
  the stdout format; neither moves anything to the other stream.
- **`--diagnostics json` makes stderr machine-readable as a whole:** one JSON object per line and
  nothing else — no tally line, no stack-trace lines.
- **The tally line** `polypiler: <e> error(s), <w> warning(s)` is written in text format, after a
  command ran and reported at least one diagnostic. A command line refused by the grammar never
  reached a command: its stderr is the one diagnostic and nothing more.
- **`--quiet`** drops infos, warnings and the tally line from stderr. It never changes stdout and
  never changes the exit code.
- **A failure leaves no half result.** After a refusal stdout is empty. A command that fails later
  has written only what it really produced; it never prints a summary line that claims a state it did
  not reach.
- **Nothing else is written:** no banner, no progress, no colour or other escape sequences, on either
  stream.

The exit code is the one ADR 0003 derives from the diagnostics; it does not depend on either format
option.

## Recorded command line

Two different strings are kept, and they must not be confused.

**The invocation as typed** is what a diagnostic carries as its reproducible command (PRD §10.1):
`polypiler` followed by every argument in the order given, each quoted by the rule below. It is not
normalised, because it must describe command lines that could not be parsed.

**The canonical command line** is what a manifest stores as the requested configuration (PRD §10.2).
Two invocations that request the same thing give the same string. It is built from the parsed
command line, in this order:

1. the command name;
2. the operands, in the order given;
3. the command's own options, ordered by name (ordinal);
4. the pipeline options in their fixed order: `--opt`, `--disable-opt`, `--assume`, `--strict`;
5. the contributed options, ordered by name (ordinal).

with these rules:

- **Not recorded:** `--diagnostics`, `--quiet`, `--output` and `--force`. They decide how a run reports
  and where its result is put, not what the result is; the manifest lies in the output directory it
  would name. Every other option is recorded unless its owner declares otherwise.
- **Spelling:** always `--name value`. A flag is written when set. A list is written once,
  comma-joined; a set is sorted (ordinal) with duplicates removed, a sequence keeps its order. A
  repeat-only option is written once per value, the values sorted (ordinal).
- **Defaults are written out.** A single-valued option that has a default is recorded with its
  effective value whether it was typed or not (`--opt balanced`), including a default resolved from
  the specification (`--source-profile`, `--target-profile`). The record then still reproduces the run
  after a default changes.
- **Values in their normal spelling.** An option whose value can be written in several ways —
  addresses, integers, assumption facts — defines one normal spelling, and that one is recorded
  (`Sum.n in [1, 31]` for `Sum.n in [0x1,31]`).
- **Paths as typed**, with `\` replaced by `/`. They are not made absolute and not resolved: an
  absolute path would put the machine's directory layout into the manifest. `a.cs` and `./a.cs` are
  therefore different records; the input hashes of the manifest identify the file.
- **Quoting:** an argument that is not empty and consists only of ASCII letters, digits and
  `_ . / : = , + @ % -` is written bare. Anything else is written in double quotes with `\`, `"`, `$`
  and `` ` `` preceded by a backslash, so the line can be pasted into a POSIX shell.

Equivalent, and therefore recorded identically: any order of options and operands; `--name value` and
`--name=value`; a comma list and its repetition; duplicates in a set; a global option anywhere or
absent; a default typed out or left out; any `--output` and `--force`.

## Parsing library

System.CommandLine supplies the command tree, tokenisation of `--name value` and `--name=value`, the
`--` terminator, case-sensitive matching and typed option access. The decision keeps what the
foundation, global-options and option-registry issues already specify against its types.

It is used with these settings, each of which the prototype needed:

| Setting | Why |
| --- | --- |
| Root is a plain `Command`, not `RootCommand` | `RootCommand` adds `--version`, the help aliases `-?`, `/h`, `/?` and the `[suggest]` directive, none of which is in the grammar |
| `HelpOption("--help", "-h")` | the only two help spellings |
| `EnablePosixBundling = false` | no short options |
| `ResponseFileTokenReplacer = null` | rule 7; the default reads `@file` and fails when it does not exist |
| Parse, then inspect the result; never `Invoke` on a result with errors | the library's own error text and its exit code 1 must not reach the user |

Four behaviours of the library cannot be switched off (2.0.15 has no setting beyond the two above) and
are closed by the foundation on the raw arguments, each with a test of its own:

| Library behaviour | Example | Required |
| --- | --- | --- |
| `:` separates name and value | `--output:out` is `--output out` | refused, unknown option |
| The next argument is always the value | `--output --force` writes into a directory named `--force` | refused, the option requires a value |
| An unknown option fills a free operand | `translate --nope` takes `--nope` as `<input>` | refused, unknown option |
| `--name=` is reported as a missing value | `--assume=` | the empty value, as `--assume ""` |

A parse error of the library carries a message and a symbol and no kind, and its messages are the
library's resources. The foundation therefore derives every refusal from the parse result and the
raw arguments, and takes no decision from a library message. The library's help layout is not
adjustable (`HelpBuilder` is internal): the root help and any command help that groups options by
owner are written by the foundation.

The library is pinned to an exact 2.0.x version. An update re-runs the tests of the two tables above;
if one of the settings stops working, or the list of closed behaviours has to grow, the hand-written
parser of the prototype is the fallback and this section is superseded.

## Adding a command or an option later

- **A command** is one `word`, registered in the single command registry by its own issue. It states
  its operands, its options with kind, default and allowed values, what it writes to stdout and its
  summary line. `help` cannot be a command; a help topic cannot take a command's name.
- **An option on a command of another area** is registered through the option registry, never by
  editing the command. Its issue states: name, kind, set or sequence, default, allowed values, whether
  it is recorded, and the normal spelling of its value. A name already used by any command keeps the
  meaning it has there.
- **A global option** needs an amendment of this ADR. It qualifies only if it applies to every command
  and changes reporting, never the result.
- **Adding** a command, an option, an allowed value or an identifier is compatible. **Renaming or
  removing** one, changing an option's kind, or making an optional option required breaks scripts and
  needs an ADR that supersedes the affected part of this one. **Changing a default** is compatible for
  recorded manifests, because defaults are written out, and is called out in the changelog.
- **Never added:** short options, abbreviations, optional values, a second spelling of an existing
  name, an environment variable or file that changes the meaning of a command line.

## Evidence

### The invocations of PRD §10

All eighteen parse under the grammar without a special case, in both prototypes. Their options
classify as:

| Group | Options appearing in PRD §10 |
| --- | --- |
| Global | none |
| Command | `--from`, `--source-profile`, `--to`, `--target-profile`, `--export`, `--verify`, `--output`, `--show`, `--against`, `--language`; pipeline: `--opt` |
| Contributed by a later area | `--cpu`, `--entry`, `--memory-map`, `--verify-emulator` (machine code and emulators), `--strategy`, `--native-provider` (adapters), `--target-triple`, `--llvm-version` (LLVM) |

`--verify` is a flag and `--verify-emulator <name>` a single-valued option: two options, not one
option with an optional value. `--show types,ranges,effects,patterns,ir,proofs` is a list declared as
a set. `"Floyd-Steinberg"` and `color.dithering.floyd-steinberg` are operands.

### Prototype

Two throw-away implementations of the grammar, not part of the product: one on System.CommandLine
2.0.15, one hand-written. Both read one table of the eight commands with their options as the command
issues state them (with `--load-address` optional, see "Consequences for open issues"), keep the
`Run(string[] args, TextWriter output, TextWriter error)` seam, and print the canonical command line
of a valid invocation. A third, on Spectre.Console.Cli 0.57.2, implements `--version` and `translate`
only. The sources are in [`evidence/0004/`](evidence/0004/); everything quoted below is in
[`evidence/0004/outputs.txt`](evidence/0004/outputs.txt), produced by
[`evidence/0004/run.sh`](evidence/0004/run.sh) on 2026-11-13.

One suite of 79 checks ran against the first two, with byte-identical results:

| Checks | Count | System.CommandLine | Hand-written |
| --- | --- | --- | --- |
| The existing tests of `Polypiler.Tests/CliTests.cs`, unchanged in intent | 9 | pass | pass |
| PRD §10 invocations | 18 | pass | pass |
| Groups of equivalent spellings that must give one canonical line | 4 groups, 11 lines | pass | pass |
| Accepted edge cases (`--`, attached negative value, empty value, `:` inside a value, `-` operand, quoting, `\` in a path) | 10 | pass | pass |
| Refused command lines (every class listed under rule 10; exclusive options are not modelled) | 27 | pass | pass |
| Help precedence and `help <command>` | 11 | pass | pass |

The existing seam and its tests survive in both. The one existing assertion that changes is the
expected one: `translate` stops being an unknown command once it exists.

The Spectre prototype does not keep the seam: it writes parse errors to the same console as results,
exits with 255, accepts an unknown option silently in its default mode, lets the last of two `--opt`
win and accepts `--force=true`.

### Measurements

Linux x64, .NET SDK 10.0.112, Intel Core i9-11950H; published with the flags of
`.github/workflows/_build.yml` (self-contained, single file, trimmed `partial`, compressed).
Start-up is wall-clock for the whole process, median of 60 interleaved runs after 5 warm-up runs.

| | Current CLI | Hand-written | System.CommandLine | Spectre.Console.Cli |
| --- | --- | --- | --- | --- |
| Published size, bytes | 13 008 536 | 13 041 881 | 13 277 084 | 15 070 043 |
| Size over hand-written | | | +235 203 (+1.8 %) | +2 028 162 (+15.6 %) |
| `--version`, single file, ms | 48.2 | 43.8 | 45.9 | 204.5 |
| `--version`, framework-dependent, ms | 17.2 | 17.8 | 19.2 | 95.4 |
| `translate`, single file, ms | | 69.2 | 109.9 | 191.3 |
| `translate`, framework-dependent, ms | | 26.3 | 45.6 | 85.1 |
| Parser code in the prototype, lines | | 98 | 153 | |
| Trim warnings | none | none | none | IL2104 |

The `translate` rows time
`translate a.cs --from csharp --to javascript --export A,B --opt speed --verify --output out`, which
parses the line and prints its canonical form. `--version` costs the same with and without
System.CommandLine because both prototypes answer it before the parser is built; that order is part
of the decision. A parsed command pays about 41 ms (single file) or 19 ms (framework-dependent) for
loading and running the library. No budget exists for these numbers yet; they are recorded for the
performance-budget ADR.

### Normalisation

The four groups of the suite cover every equivalence listed under "Recorded command line". Five spellings of
one request — different option order, `=` and space, comma list and repetition with a duplicate,
`--quiet` and `--diagnostics json` before and after the command, a different `--output` with
`--force`, the operand after `--` — give

```
translate a.cs --export A,B --from csharp --to javascript --opt speed --disable-opt O0001,O0100
```

and `--opt balanced` typed or omitted, `--show ir` typed or omitted, and two `--assume` facts in
either order give one line each.

### Conventions of other tools

Where their documentation agrees, the grammar follows it. The .NET column cites the
System.CommandLine articles "Syntax overview: Commands, options, and arguments" and "Design guidance".

| Decision | git | cargo | .NET |
| --- | --- | --- | --- |
| Lower-case names, words joined by hyphens | | | "Design guidance": lowercase names, kebab case names |
| `--name value` and `--name=value` | `gitcli(7)`, which recommends the attached form in scripts | | "Syntax overview", option-argument delimiters |
| `--` ends option parsing | | | "Syntax overview", the `--` token (POSIX convention) |
| Names are case-sensitive | | | "Syntax overview", case sensitivity (POSIX convention) |
| `help <command>` beside `<command> --help` | `git-help(1)` | `cargo(1)` | |
| A list as a comma list or by repetition | | `cargo-build(1)`, `--features` | "Syntax overview", multiple arguments: repeat the option |
| A negative flag is `--no-<name>` | `gitcli(7)`, negating options | | |
| `--quiet` silences log output | | `cargo(1)` | |
| Few short options | | | "Design guidance": minimise short-form aliases |

Where they differ, the stricter or the more tolerant reading is chosen on purpose:

- **Position of tool-wide options.** `git(1)` and `cargo(1)` write them before the command
  (`git [options] <command> [<args>]`, `cargo [options] command [args]`), `dotnet(1)` after it
  (`dotnet <COMMAND> [--verbosity <LEVEL>] [command-options] [arguments]`). Both positions are
  accepted for the two global options.
- **Abbreviations.** `gitcli(7)` allows a long option to be shortened to a unique prefix and in the
  same paragraph tells scripts not to, because a later option can make the prefix ambiguous. They are
  not accepted at all.
- **Delimiters.** System.CommandLine accepts `:` as a third delimiter; git and cargo do not. It is
  refused.
- **JSON messages.** `cargo-build(1)` writes `--message-format json` to stdout. Here diagnostics stay
  on stderr in both formats, so that stdout holds the result alone.

## Rejected alternatives

- **Hand-written parser.** It met the grammar with fewer lines (98 against 153), 235 KB less and
  about 41 ms less per parsed command, and needs none of the four corrections. It is rejected because
  three ready issues are specified against System.CommandLine's types, and because what lies beyond
  the first release — the completion protocol, typed value conversion, response files should they be
  wanted — would then be written and maintained here. The margin is small enough that the reversal
  condition in "Parsing library" is meant seriously.
- **Spectre.Console.Cli.** Two megabytes and 60–160 ms more at start-up, trim warnings, one output
  stream for results and errors, and lenient defaults that have to be found and switched off one by
  one. Its strength is rich console rendering, which the output contract forbids.
- **`--opt`, `--disable-opt`, `--assume` and `--strict` as global options.** Refused for the reason
  given under "Options"; only the two reporting options are global.
- **Options only before the command, or globals only after it.** `polypiler translate … --quiet` and
  `polypiler --diagnostics json translate …` are both what a user or a wrapper script naturally
  writes, and accepting both costs nothing in the canonical form, which records neither.
- **Repetition only, or comma list only.** PRD §10 writes `--show types,ranges`, and a script that
  builds a command line appends `--export X` per item. The canonical form makes the difference
  disappear.
- **Everything on stdout unless `--diagnostics json`.** A report piped into another tool would carry
  warnings in its data, and the stream layout would depend on a format option.
- **Aliases and inference of `--from`.** See "Identifiers". An alias is also a second name that has
  to stay unique against every identifier added later, in a namespace that already holds `c`, `cpp`,
  `csharp` and `c64-basic-v2`.
- **A configuration file beside the input.** The manifest records one command line. A file adds a
  second source of options whose content, location and precedence would all have to be recorded for
  the run to be reproducible, and nothing in PRD §10 needs it.

## Deferred

Each of these is left out of the first release and needs an issue of its own before any work:

- **Shell completion.** System.CommandLine has a completion protocol (`[suggest]` directive); it is
  switched off with the plain root command. An issue would decide which shells, how the scripts are
  installed and whether identifiers from `spec/` are completed.
- **Configuration file.** Only together with a rule for how the effective options are recorded: the
  canonical command line must stay the complete request.
- **Identifier aliases.** Only as data in the language documents (one `aliases` list per identifier),
  normalised to the identifier in the canonical command line and reported when used.
- **Response files.** Only if a real command line exceeds an operating-system limit; the expanded
  arguments, not the file name, would be recorded.

## Consequences for open issues

The decision agrees with the open CLI issues except where listed. Each line is an edit to the named
issue.

| Issue | Change |
| --- | --- |
| [#25](https://github.com/Hawkynt/Polypiler/issues/25) `CLI-FOUNDATION` | "Shared options are accepted before or after the command name" applies to `--diagnostics` and `--quiet` only. Add the refusals it does not list — flag given a value or given twice, missing required option, missing or surplus operand, empty list item, no command, value starting with `-` not attached — with message texts and test rows; add tests for the five settings and the four closed library behaviours; add `help <command>`, help precedence (rule 9) and `--version` as the sole argument; state that `--quiet` leaves stdout alone and that `--diagnostics json` leaves only JSON on stderr. |
| [#23](https://github.com/Hawkynt/Polypiler/issues/23) `CLI-EXITCODES` | The tally line is written in text format only and only after a command ran. As written it would follow the single `PLY0002` line that #25 requires to be the whole of stderr, and it would break the one-JSON-object-per-line stream. |
| [#179](https://github.com/Hawkynt/Polypiler/issues/179) `CLI-OPTIONS` | They are command options of the pipeline commands, not global: `polypiler --opt speed translate …` is `PLY0003`. An empty item in `--disable-opt` is refused by the foundation (`PLY0003`) before the ID check. |
| [#178](https://github.com/Hawkynt/Polypiler/issues/178) `CLI-OPTION-REGISTRY` | A contributor declares for each option its kind, set or sequence, whether it is recorded, and the normal spelling of its value. The planned register lacks `--target-triple` and `--llvm-version`, both in PRD §10. The register is the one list of owners. |
| [#180](https://github.com/Hawkynt/Polypiler/issues/180) `CLI-TRANSLATE` | `requestedConfig` is the canonical command line: own options ordered by name, defaults written out (profiles included), `--output` and `--force` left out. Its out-of-scope list names other owners for `--cpu`, `--entry`, `--memory-map` and `--verify-emulator` than the register of #178 does; the register wins. |
| [#880](https://github.com/Hawkynt/Polypiler/issues/880) `CLI-RECOVER` | It makes `--load-address` required; the `recover` example of PRD §10 has none and would be refused. Either the option gets a default or the PRD example is revised in the same change. It also has to name the normal spelling of an address for the canonical command line. |
| [#635](https://github.com/Hawkynt/Polypiler/issues/635) `CLI-PACKAGE` | `--publish` and `--no-publish` are a declared exclusive pair: both together are `PLY0011`, ahead of `PLY0080`. |
| [#17](https://github.com/Hawkynt/Polypiler/issues/17) `ADR-DIAGNOSTICS` | The `command` field of a diagnostic is the invocation as typed, quoted by the rule of "Recorded command line". |

`inspect`, `verify`, `capabilities`, `search` and `generate` need no change: their `--format`, their
summary lines and their use of `PLY0003` and `PLY0006` are what this ADR states.
