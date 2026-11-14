# ADR 0003 — Diagnostics registry, renderings and exit codes

Area: foundation

| | |
| --- | --- |
| Status | accepted |
| Index | [decision records](README.md) |
| Date | 2026-11-14 |
| Issue | [#17](https://github.com/Hawkynt/Polypiler/issues/17) |
| Requirements | [PRD §10.1](../prd/04-roadmap-and-delivery.md#s10-1), [§7.2](../prd/03-behavior-and-testing.md#s7-2) |

## Question

How does every refusal Polypiler makes become recognisable by a script — a stable code, a stable exit
code and a machine-readable record — and how do several hundred issues allocate codes without two of
them claiming the same one?

## Decision

1. **Codes.** A diagnostic code is `PLY` and four digits. A code means one thing forever: it is never
   renumbered and never reused. A code that stops being emitted stays in the registry with
   `retired: true`.
2. **One registry.** [`spec/diagnostics.yaml`](../../spec/diagnostics.yaml) lists every code once, in
   ascending order, with its severity, exit code, message, rule ID, area and owning issue. It is the
   code table of this record; nothing copies it. Its `blocks` are the ranges of `backlog/README.md`
   plus one sub-block per language and per format family; a code belongs to the innermost block that
   contains it and must name that block's area. A code an issue has allocated but not yet implemented
   is `reserved: true`.
3. **Declared in code.** Every code something emits is a `static readonly DiagnosticDescriptor` field
   marked `[Diagnostic]`, next to the code that emits it. The registry test loads every Polypiler
   assembly, collects the descriptors and fails on a descriptor the registry lacks, on a severity,
   exit code or rule that differs, on a declared code the registry calls reserved or retired, on a
   registered code nothing declares unless it is reserved or retired, and on a `const string` holding
   a code that no descriptor declares. `polypiler-tooling diagnostics validate [--registry <path>]`
   checks the registry by itself; each problem is `PLY0045`.
4. **Severities.** `info`, `warning`, `error`. Only an error has an exit code; a warning may be
   `strict`, which `--strict` turns into an error.
5. **Exit codes.** One per category of PRD §10.1, numbered in the order the PRD lists them, `70` from
   BSD `sysexits.h` (`EX_SOFTWARE`):

   | Code | `ExitCode` | Meaning |
   | --- | --- | --- |
   | 0 | `Success` | no error was reported |
   | 1 | `EnvironmentError` | a file cannot be read or written, a toolchain is missing, a repository file is invalid |
   | 2 | `UsageError` | the command line cannot be understood |
   | 3 | `UnsupportedConstruct` | the input uses a construct outside the supported subset, or one the target cannot express |
   | 4 | `InvalidSource` | the input is not valid in its own language |
   | 5 | `SemanticMismatch` | the translation computes something other than the source |
   | 6 | `TargetBuildFailure` | the generated code does not build |
   | 7 | `OracleFailure` | the reference oracle failed or is missing |
   | 8 | `LicenseBlocker` | a licence forbids the use or cannot be determined |
   | 9 | `MissingDependency` | a native or model dependency is missing |
   | 10 | `BudgetExceeded` | a time, memory, output or proof budget was exceeded |
   | 70 | `InternalError` | Polypiler itself failed |

   3 and 4 are the numbers the prototype has used since its first release and the README documents;
   the backlog issues written before it had them the other way round, and follow this table.
6. **Precedence.** A run's exit code is `Success` without an error; otherwise the exit code of the
   **first** error in emission order — not the highest number. An error without an exit code is a
   defect and counts as `InternalError`. Once `PLY0001`, the internal compiler error, is reported, the
   exit code is `InternalError` whatever came before or after. Warnings and infos never change it.
   `DiagnosticBag.ExitCode` implements the rule; no command returns a number that bypasses it.
7. **Text rendering.** One line in the MSBuild canonical error format:
   `<file>(<startLine>,<startColumn>): <severity> <code>: <message>` with a source range,
   `polypiler: <severity> <code>: <message>` without. The severity is lower-case; every `\n` and `\r`
   of the message becomes a space.
8. **JSON rendering.** One object per line, UTF-8, no indentation, keys in this order: `code`,
   `severity`, `message`, `source` (`file`, `startLine`, `startColumn`, `endLine`, `endColumn` —
   1-based, the end inclusive — or `null`), `rule`, `cause`, `workaround`, `requiredCapability`,
   `command`. Absent fields are `null`. Only what RFC 8259 requires is escaped: the quotation mark,
   the backslash and the control characters; non-ASCII characters are written as they are. `command`
   is the invocation as typed, quoted by the rule of [ADR 0004](0004-cli-design.md#recorded-command-line);
   the renderer carries it unchanged.

## Consequences

- The types live in `Polypiler.Core`, which has no package dependency: `DiagnosticSeverity`,
  `ExitCode`, `SourceRange`, `DiagnosticDescriptor`, `DiagnosticAttribute`, `Diagnostic` (whose
  init-only members carry the structured fields; `Diagnostic.Create` fills them from a descriptor),
  `IDiagnosticSink`, `DiagnosticBag` and `DiagnosticRenderer`. Reading the YAML registry needs
  YamlDotNet and lives in `Polypiler.Tooling.Diagnostics`.
- The translate command's refusals of a command line are `PLY0003`, as ADR 0004 assigns; `PLY0001`
  is the internal compiler error only.
- An issue that raises a new code registers it in the same change, in its area's block, with the
  descriptor that declares it. An issue that allocates a code for later registers it `reserved`; the
  issue that implements it removes the flag.
- The CLI's own exit-code constants are the `ExitCode` members. Routing every command through
  `DiagnosticBag`, the `--diagnostics json` option and the top-level exception handler that reports
  `PLY0001` belong to the CLI foundation and its exit-code follow-up, not to this record.
