# Exit codes

Generated from `ExitCodeTable` in `Polypiler.Cli`; `polypiler help exit-codes` prints the same table.
Do not edit it by hand: a test regenerates it and fails when this file differs.

The exit code of a run is the one of the first error it reported; an internal compiler error
(`PLY0001`) makes it 70 whatever else was reported, and warnings and infos never change it
([ADR 0003](../adr/0003-diagnostics-and-exit-codes.md)). `spec/diagnostics.yaml` names the exit
code of every diagnostic.

| Code | `ExitCode` | Meaning |
| ---: | --- | --- |
| 0 | `Success` | success; verification passed if it was requested |
| 1 | `EnvironmentError` | input, output, toolchain or repository data unusable |
| 2 | `UsageError` | the command line is not valid |
| 3 | `UnsupportedConstruct` | a construct is outside the supported subset |
| 4 | `InvalidSource` | the source is not valid in its language |
| 5 | `SemanticMismatch` | generated code disagrees with the reference |
| 6 | `TargetBuildFailure` | the target toolchain rejected the generated code |
| 7 | `OracleFailure` | the reference could not produce a result |
| 8 | `LicenseBlocker` | a licence forbids the requested output |
| 9 | `MissingDependency` | a native library or model is missing |
| 10 | `BudgetExceeded` | a time, memory, size or proof budget was exceeded |
| 70 | `InternalError` | internal compiler error |
| 130 | — | cancelled by the user |
