<!-- generated from spec/diagnostics.yaml by "report errors"; do not edit -->
# Error catalogue

Every diagnostic Polypiler reports carries a code, `PLY` and four digits, that means one thing forever. Each area allocates its codes from a block of its own; the tables below list every registered code by block with its severity, the exit code it produces and the issue that owns it, and each code links to a page with its cause, an example and what to do about it. Only an error sets the exit code; a warning does so only where `--strict` turns it into an error. A *reserved* code is allocated to an issue that is not implemented yet; a *retired* code is no longer reported and is never given to anything else. The pages are generated from [`spec/diagnostics.yaml`](../../spec/diagnostics.yaml).

## PLY0001–PLY0099 · infrastructure

| Code | Severity | Exit code | Message | Owner |
| --- | --- | --- | --- | --- |
| [PLY0004](PLY0004.md) | error | 1 (EnvironmentError) | `cannot read input '{0}'` | CLI-FOUNDATION |
| [PLY0009](PLY0009.md) | warning | — (3 with --strict) | `assumption '{0}' is trusted without proof` | CLI-OPTIONS |
| [PLY0031](PLY0031.md) | error | 10 (BudgetExceeded) | `time limit of {0} ms exceeded` | INFRA-RUNNER |
| ~~[PLY0035](PLY0035.md)~~ (retired) | error | 1 (EnvironmentError) | `the sandbox of '{0}' was lost` | INFRA-RUNNER |
