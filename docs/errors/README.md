<!-- generated from spec/diagnostics.yaml by "report errors"; do not edit -->
# Error catalogue

Every diagnostic Polypiler reports carries a code, `PLY` and four digits, that means one thing forever. Each area allocates its codes from a block of its own; the tables below list every registered code by block with its severity, the exit code it produces and the issue that owns it, and each code links to a page with its cause, an example and what to do about it. Only an error sets the exit code; a warning does so only where `--strict` turns it into an error. A *reserved* code is allocated to an issue that is not implemented yet; a *retired* code is no longer reported and is never given to anything else. The pages are generated from [`spec/diagnostics.yaml`](../../spec/diagnostics.yaml).

## PLY0001–PLY0099 · infrastructure

| Code | Severity | Exit code | Message | Owner |
| --- | --- | --- | --- | --- |
| [PLY0001](PLY0001.md) | error | 70 (InternalError) | `internal compiler error: <exception>` | ADR-DIAGNOSTICS |
| [PLY0002](PLY0002.md) | error | 2 (UsageError) | `unknown command '<word>'; run 'polypiler --help'` | CLI-FOUNDATION |
| [PLY0003](PLY0003.md) | error | 2 (UsageError) | `<reason>` | INFRA-TOOLCHAINS |
| [PLY0004](PLY0004.md) | error | 1 (EnvironmentError) | `cannot read the toolchain lock: <reason>` | INFRA-TOOLCHAINS |
| [PLY0005](PLY0005.md) | error | 1 (EnvironmentError) | `output directory '<path>' is not empty; pass --force to write into it` | CLI-FOUNDATION |
| [PLY0006](PLY0006.md) (reserved) | error | 2 (UsageError) | `unknown <kind> '<identifier>'` | CLI-TRANSLATE |
| [PLY0007](PLY0007.md) (reserved) | error | 2 (UsageError) | `'<assumption>' is not an assumption: <reason>` | CLI-OPTIONS |
| [PLY0008](PLY0008.md) (reserved) | error | 2 (UsageError) | `'<id>' is not an optimization ID (expected O followed by four digits)` | CLI-OPTIONS |
| [PLY0009](PLY0009.md) (reserved) | warning | — (3 with --strict) | `assumption '<assumption>' is trusted, not proven` | CLI-OPTIONS |
| [PLY0010](PLY0010.md) (reserved) | error | 3 (UnsupportedConstruct) | `<code>: <message> (refused by --strict)` | CLI-OPTIONS |
| [PLY0011](PLY0011.md) | error | 2 (UsageError) | `options '<first>' and '<second>' cannot be combined` | CLI-FOUNDATION |
| [PLY0012](PLY0012.md) (reserved) | error | 2 (UsageError) | `'<level>' is not an optimization level; expected one of: <levels>` | CLI-OPTIONS |
| [PLY0013](PLY0013.md) (reserved) | error | 4 (InvalidSource) | `export '<name>' not found in '<file>'` | CLI-TRANSLATE |
| [PLY0014](PLY0014.md) (reserved) | error | 3 (UnsupportedConstruct) | `no <frontend\|backend> for '<language>'` | CLI-TRANSLATE |
| [PLY0015](PLY0015.md) (reserved) | error | 2 (UsageError) | `unknown facet '<facet>'` | CLI-INSPECT |
| [PLY0016](PLY0016.md) (reserved) | error | 1 (EnvironmentError) | `no manifest in '<path>'` | CLI-VERIFY |
| [PLY0017](PLY0017.md) (reserved) | error | 2 (UsageError) | `'<path>' holds no test vectors: <reason>` | CLI-VERIFY |
| [PLY0018](PLY0018.md) (reserved) | error | 1 (EnvironmentError) | `catalogue index '<path>' not found` | CLI-SEARCH |
| [PLY0019](PLY0019.md) (reserved) | error | 2 (UsageError) | `unknown algorithm '<id>'` | CLI-GENERATE |
| [PLY0020](PLY0020.md) | error | 1 (EnvironmentError) | `<reason>` | INFRA-TOOLCHAINS |
| [PLY0021](PLY0021.md) | error | 1 (EnvironmentError) | `toolchain '<key>' is not installed (probe: <command>)[: <cause>]` | INFRA-TOOLCHAINS |
| [PLY0022](PLY0022.md) | error | 1 (EnvironmentError) | `toolchain '<key>' is <actual>, the lock pins <expected>[: <cause>]` | INFRA-TOOLCHAINS |
| [PLY0023](PLY0023.md) | error | 1 (EnvironmentError) | `corpus file '<repository>:<path>' has SHA-256 <actual>, the lock records <expected>[ (<n> bytes, the lock records <m> bytes)]` | INFRA-CORPUS |
| [PLY0024](PLY0024.md) (reserved) | error | 8 (LicenseBlocker) | `the licence '<licence>' of '<component>' is not approved` | INFRA-LICENSES |
| [PLY0025](PLY0025.md) | error | 1 (EnvironmentError) | `'<path>' does not exist in <repository> at <commit>[: <cause>]` | INFRA-CORPUS |
| [PLY0026](PLY0026.md) | error | 1 (EnvironmentError) | `corpus manifest invalid: <reason>` | INFRA-CORPUS |
| [PLY0027](PLY0027.md) (reserved) | error | 1 (EnvironmentError) | `vector file '<path>' was modified` | INFRA-INTEGRITY |
| [PLY0028](PLY0028.md) (reserved) | error | 1 (EnvironmentError) | `'<path>': <reason>` | INFRA-INTEGRITY |
| [PLY0029](PLY0029.md) (reserved) | error | 8 (LicenseBlocker) | `the licence of '<component>' cannot be determined: <reason>` | INFRA-LICENSES |
| [PLY0030](PLY0030.md) | error | 6 (TargetBuildFailure) | `target build failed: '<toolchain> <arguments>' exited with <code>` | INFRA-RUNNER |
| [PLY0031](PLY0031.md) | error | 10 (BudgetExceeded) | `time limit of <milliseconds> ms exceeded` | INFRA-RUNNER |
| [PLY0032](PLY0032.md) | error | 10 (BudgetExceeded) | `memory limit of <bytes> bytes exceeded` | INFRA-RUNNER |
| [PLY0033](PLY0033.md) | error | 10 (BudgetExceeded) | `output limit of <bytes> bytes exceeded` | INFRA-RUNNER |
| [PLY0034](PLY0034.md) | error | 1 (EnvironmentError) | `isolation is unavailable on this host; untrusted input was not executed` | INFRA-RUNNER |
| [PLY0036](PLY0036.md) (reserved) | error | 1 (EnvironmentError) | `rebuild not deterministic: '<path>' differs at byte <offset>` | INFRA-REBUILD |
| [PLY0037](PLY0037.md) (reserved) | error | 8 (LicenseBlocker) | `SBOM component '<component>' lacks a licence` | INFRA-SBOM |
| [PLY0038](PLY0038.md) | error | 1 (EnvironmentError) | `<from> must not reference <to> (rule: <rule>)` | INFRA-MODULES |
| [PLY0040](PLY0040.md) | error | 1 (EnvironmentError) | `<json pointer>: <schema keyword message>` | SPEC-META |
| [PLY0041](PLY0041.md) | error | 1 (EnvironmentError) | `<reason>` | SPEC-META |
| [PLY0042](PLY0042.md) | error | 1 (EnvironmentError) | `duplicate spec id '<id>', first defined in <path>` | SPEC-META |
| [PLY0043](PLY0043.md) | error | 1 (EnvironmentError) | `<reason>` | SPEC-META |
| [PLY0044](PLY0044.md) | error | 1 (EnvironmentError) | `<reason>` | SPEC-META |
| [PLY0045](PLY0045.md) | error | 1 (EnvironmentError) | `<reason>` | ADR-DIAGNOSTICS |
| [PLY0046](PLY0046.md) | error | 1 (EnvironmentError) | `<reason> (rule: <rule>)` | SPEC-META |
| [PLY0047](PLY0047.md) | error | 1 (EnvironmentError) | `<reason>` | SPEC-META |
| [PLY0050](PLY0050.md) (reserved) | error | 7 (OracleFailure) | `oracle '<oracle>' failed: <reason>` | VER-ORACLE |
| [PLY0051](PLY0051.md) (reserved) | error | 5 (SemanticMismatch) | `<function>(<arguments>) returned <actual>, the oracle <expected>` | VER-ORACLE |
| [PLY0052](PLY0052.md) (reserved) | error | 5 (SemanticMismatch) | `<function>(<arguments>) returned <actual>, <ulps> ulp from the oracle's <expected>` | VER-ORACLE |
| [PLY0053](PLY0053.md) (reserved) | error | 1 (EnvironmentError) | `'<path>' is not a valid manifest: <reason>` | VER-MANIFEST |
| [PLY0054](PLY0054.md) (reserved) | error | 1 (EnvironmentError) | `'<path>' listed in the manifest is <missing\|changed>` | VER-MANIFEST |
| [PLY0055](PLY0055.md) (reserved) | warning | — | `proof of <rule> gave up after <budget>` | VER-SMT |
| [PLY0056](PLY0056.md) (reserved) | error | 5 (SemanticMismatch) | `proof of <rule> refuted: <name>=<hex>[, ...]: before <hex>, after <hex>` | VER-SMT |
| [PLY0057](PLY0057.md) (reserved) | error | 10 (BudgetExceeded) | `exhaustive proof of <rule> needs <n> input bits, budget is <m>` | VER-EXHAUSTIVE |
| [PLY0058](PLY0058.md) (reserved) | error | 1 (EnvironmentError) | `<reason>` | VER-GATES |
| [PLY0059](PLY0059.md) (reserved) | error | 5 (SemanticMismatch) | `round trip '<id>' leg <leg> (<opt>): <function>(<arguments>) expected <expected>, got <actual>` | VER-ROUNDTRIP |
| [PLY0060](PLY0060.md) (reserved) | error | 1 (EnvironmentError) | `<reason>` | VER-EVIDENCE |
| [PLY0061](PLY0061.md) (reserved) | error | 1 (EnvironmentError) | `<reason>` | SPEC-CAPABILITIES |
| [PLY0062](PLY0062.md) (reserved) | error | 3 (UnsupportedConstruct) | `the proof of <rule> meets the unmodelled operation <operation>` | VER-SMT |
| [PLY0063](PLY0063.md) (reserved) | error | 7 (OracleFailure) | `no independent oracle is registered for '<fixture>'` | VER-ORACLE |
| [PLY0070](PLY0070.md) | error | 1 (EnvironmentError) | `fuzz target '<target>': <kind> '<signature>' at iteration <i> (seed <seed>); minimized to <n> bytes: artifacts/fuzz/<target>/<id>.bin` | INFRA-FUZZ |
| [PLY0071](PLY0071.md) | error | 1 (EnvironmentError) | `property '<test>' falsified after <n> inputs (seed <seed>): <shrunk> (original <original>, <k> shrink steps); replay with POLYPILER_PROPERTY_SEED=<seed>` | INFRA-PROPERTY |
| [PLY0072](PLY0072.md) (reserved) | error | 1 (EnvironmentError) | `benchmark '<id>/<variant>': environment differs from the baseline (<field>: <baseline> -> <current>)` | INFRA-BENCH |
| [PLY0073](PLY0073.md) (reserved) | error | 1 (EnvironmentError) | `benchmark '<id>/<variant>': <metric> <value> exceeds its budget <budget>` | INFRA-BENCH |
| [PLY0074](PLY0074.md) | error | 1 (EnvironmentError) | `<file>(<line>): step '<step>' has no binding` | INFRA-BDD |
| [PLY0075](PLY0075.md) | error | 1 (EnvironmentError) | `<reason>` | INFRA-CI-CORE |
| [PLY0076](PLY0076.md) | error | 1 (EnvironmentError) | `<reason>` | INFRA-CI-CORE |
| [PLY0077](PLY0077.md) | error | 1 (EnvironmentError) | `<reason>` | INFRA-FUZZ |
| [PLY0080](PLY0080.md) (reserved) | error | 2 (UsageError) | `<reason>` | CLI-PACKAGE |
| [PLY0081](PLY0081.md) | error | 9 (MissingDependency) | `native dependency '<name>' required by '<component>' is missing` | CLI-EXITCODES |
| [PLY0082](PLY0082.md) (reserved) | error | 8 (LicenseBlocker) | `<reason>` | CLI-PACKAGE |
| [PLY0083](PLY0083.md) (reserved) | error | 2 (UsageError) | `<reason>` | CLI-RECOVER |
| [PLY0084](PLY0084.md) (reserved) | error | 9 (MissingDependency) | `<reason>` | CLI-RECOVER |
| [PLY0085](PLY0085.md) (reserved) | error | 2 (UsageError) | `<reason>` | CLI-PACKAGE |
| [PLY0090](PLY0090.md) (reserved) | error | 1 (EnvironmentError) | `<reason>` | INFRA-DASHBOARD |
| [PLY0091](PLY0091.md) | error | 1 (EnvironmentError) | `<reason>` | INFRA-DEVDOCS |

## PLY0100–PLY0199 · ir

| Code | Severity | Exit code | Message | Owner |
| --- | --- | --- | --- | --- |
| [PLY0100](PLY0100.md) | error | 70 (InternalError) | `ill-typed IR in '<member>': <reason>` | PROTOTYPE |
| [PLY0101](PLY0101.md) | error | 70 (InternalError) | `<reason>` | PROTOTYPE |

## PLY1000–PLY1099 · csharp

| Code | Severity | Exit code | Message | Owner |
| --- | --- | --- | --- | --- |
| [PLY1000](PLY1000.md) | error | 4 (InvalidSource) | `<CS####>: <the compiler's message>` | PROTOTYPE |
| [PLY1002](PLY1002.md) | error | 3 (UnsupportedConstruct) | `'<construct>' is not supported yet` | PROTOTYPE |
| [PLY1003](PLY1003.md) | error | 4 (InvalidSource) | `<CS0220\|CS0221>: <the compiler's message>` | PROTOTYPE |
| [PLY1052](PLY1052.md) | error | 3 (UnsupportedConstruct) | `<reason>` | PROTOTYPE |
| [PLY1053](PLY1053.md) | error | 3 (UnsupportedConstruct) | `'<operation>' cannot be expressed in C# profile '<profile>'` | PROTOTYPE |

## PLY1100–PLY1199 · javascript

| Code | Severity | Exit code | Message | Owner |
| --- | --- | --- | --- | --- |
| [PLY1100](PLY1100.md) | error | 4 (InvalidSource) | `<the parser's message>` | PROTOTYPE |
| [PLY1101](PLY1101.md) | error | 3 (UnsupportedConstruct) | `<reason>` | PROTOTYPE |
| [PLY1102](PLY1102.md) | error | 3 (UnsupportedConstruct) | `'<construct>' is not supported yet` | PROTOTYPE |
| [PLY1103](PLY1103.md) | error | 3 (UnsupportedConstruct) | `<reason>` | PROTOTYPE |
| [PLY1104](PLY1104.md) | error | 3 (UnsupportedConstruct) | `the file has no function '<name>' to export` | PROTOTYPE |
| [PLY1105](PLY1105.md) | error | 3 (UnsupportedConstruct) | `<reason>` | PROTOTYPE |
| [PLY1106](PLY1106.md) | error | 3 (UnsupportedConstruct) | `<reason>` | PROTOTYPE |
| [PLY1152](PLY1152.md) | error | 3 (UnsupportedConstruct) | `'<operation>' cannot be expressed in JavaScript profile '<profile>'` | PROTOTYPE |
