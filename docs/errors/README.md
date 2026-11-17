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
| [PLY0007](PLY0007.md) | error | 2 (UsageError) | `'<assumption>' is not a valid assumption: <reason>` | CLI-OPTIONS |
| [PLY0008](PLY0008.md) | error | 2 (UsageError) | `'<id>' is not an optimization ID (expected O followed by four digits)` | CLI-OPTIONS |
| [PLY0009](PLY0009.md) | warning | — (3 with --strict) | `assumption '<assumption>' is trusted without proof` | CLI-OPTIONS |
| [PLY0010](PLY0010.md) | error | 3 (UnsupportedConstruct) | `strict mode: <code> <message>` | CLI-OPTIONS |
| [PLY0011](PLY0011.md) | error | 2 (UsageError) | `options '<first>' and '<second>' cannot be combined` | CLI-FOUNDATION |
| [PLY0012](PLY0012.md) | error | 2 (UsageError) | `'<level>' is not an optimization level; expected one of: none, balanced, speed, size, memory` | CLI-OPTIONS |
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
| [PLY0024](PLY0024.md) | error | 8 (LicenseBlocker) | `'<path>': licence '<expression>' is denied in <mode> mode[ (<licence> awaits an owner decision, requested by <issue>)]` | INFRA-LICENSES |
| [PLY0025](PLY0025.md) | error | 1 (EnvironmentError) | `'<path>' does not exist in <repository> at <commit>[: <cause>]` | INFRA-CORPUS |
| [PLY0026](PLY0026.md) | error | 1 (EnvironmentError) | `corpus manifest invalid: <reason>` | INFRA-CORPUS |
| [PLY0027](PLY0027.md) | error | 1 (EnvironmentError) | `golden file '<path>' was modified: <reason>` | INFRA-INTEGRITY |
| [PLY0028](PLY0028.md) | error | 1 (EnvironmentError) | `vectors '<path>' lack independent provenance: <reason>` | INFRA-INTEGRITY |
| [PLY0029](PLY0029.md) | error | 8 (LicenseBlocker) | `'<path>': no licence could be determined[: <reason>]` | INFRA-LICENSES |
| [PLY0030](PLY0030.md) | error | 6 (TargetBuildFailure) | `target build failed: '<toolchain> <arguments>' exited with <code>` | INFRA-RUNNER |
| [PLY0031](PLY0031.md) | error | 10 (BudgetExceeded) | `time limit of <milliseconds> ms exceeded` | INFRA-RUNNER |
| [PLY0032](PLY0032.md) | error | 10 (BudgetExceeded) | `memory limit of <bytes> bytes exceeded` | INFRA-RUNNER |
| [PLY0033](PLY0033.md) | error | 10 (BudgetExceeded) | `output limit of <bytes> bytes exceeded` | INFRA-RUNNER |
| [PLY0034](PLY0034.md) | error | 1 (EnvironmentError) | `isolation is unavailable on this host; untrusted input was not executed` | INFRA-RUNNER |
| [PLY0036](PLY0036.md) | error | 1 (EnvironmentError) | `rebuild not deterministic: '<path>' differs at byte <offset>` | INFRA-REBUILD |
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
| [PLY0050](PLY0050.md) | error | 7 (OracleFailure) | `reference oracle '<oracle>' failed for '<fixture>': <cause>` | VER-ORACLE |
| [PLY0051](PLY0051.md) | error | 5 (SemanticMismatch) | `'<fixture>' vector '<name>': expected <hex>, actual <hex>, first difference at byte <offset>` | VER-ORACLE |
| [PLY0052](PLY0052.md) | error | 5 (SemanticMismatch) | `'<fixture>' vector '<name>' element <index>: expected <hex>, actual <hex>, <distance> exceeds <mode> <bound>` | VER-ORACLE |
| [PLY0053](PLY0053.md) | error | 1 (EnvironmentError) | `manifest '<path>' is invalid: <JSON pointer>: <reason>` | VER-MANIFEST |
| [PLY0054](PLY0054.md) | error | 1 (EnvironmentError) | `manifest '<path>' claims '<state>' but the evidence supports only '<derived>': <missing>` | VER-MANIFEST |
| [PLY0055](PLY0055.md) (reserved) | warning | — | `proof of <rule> gave up after <budget>` | VER-SMT |
| [PLY0056](PLY0056.md) (reserved) | error | 5 (SemanticMismatch) | `proof of <rule> refuted: <name>=<hex>[, ...]: before <hex>, after <hex>` | VER-SMT |
| [PLY0057](PLY0057.md) (reserved) | error | 10 (BudgetExceeded) | `exhaustive proof of <rule> needs <n> input bits, budget is <m>` | VER-EXHAUSTIVE |
| [PLY0058](PLY0058.md) | error | 1 (EnvironmentError) | `gate record '<file>': fixture '<fixture>' claims '<state>': <reason>` | VER-GATES |
| [PLY0059](PLY0059.md) (reserved) | error | 5 (SemanticMismatch) | `round trip '<id>' leg <leg> (<opt>): <function>(<arguments>) expected <expected>, got <actual>` | VER-ROUNDTRIP |
| [PLY0060](PLY0060.md) | error | 1 (EnvironmentError) | `evidence bundle '<directory>' <reason>` | VER-EVIDENCE |
| [PLY0061](PLY0061.md) (reserved) | error | 1 (EnvironmentError) | `<reason>` | SPEC-CAPABILITIES |
| [PLY0062](PLY0062.md) (reserved) | error | 3 (UnsupportedConstruct) | `the proof of <rule> meets the unmodelled operation <operation>` | VER-SMT |
| [PLY0063](PLY0063.md) | error | 7 (OracleFailure) | `no independent oracle is registered for '<fixture>'` | VER-ORACLE |
| [PLY0064](PLY0064.md) | error | 2 (UsageError) | `<reason>` | VER-IMAGES |
| [PLY0070](PLY0070.md) | error | 1 (EnvironmentError) | `fuzz target '<target>': <kind> '<signature>' at iteration <i> (seed <seed>); minimized to <n> bytes: artifacts/fuzz/<target>/<id>.bin` | INFRA-FUZZ |
| [PLY0071](PLY0071.md) | error | 1 (EnvironmentError) | `property '<test>' falsified after <n> inputs (seed <seed>): <shrunk> (original <original>, <k> shrink steps); replay with POLYPILER_PROPERTY_SEED=<seed>` | INFRA-PROPERTY |
| [PLY0072](PLY0072.md) | error | 1 (EnvironmentError) | `benchmark '<id>/<variant>': <reason>` | INFRA-BENCH |
| [PLY0073](PLY0073.md) | error | 1 (EnvironmentError) | `benchmark '<id>/<variant>': <metric> regressed by <p> % (baseline <b>, current <c>), budget <m> % from <budget file>` | INFRA-BENCH |
| [PLY0074](PLY0074.md) | error | 1 (EnvironmentError) | `<file>(<line>): step '<step>' has no binding` | INFRA-BDD |
| [PLY0075](PLY0075.md) | error | 1 (EnvironmentError) | `<reason>` | INFRA-CI-CORE |
| [PLY0076](PLY0076.md) | error | 1 (EnvironmentError) | `<reason>` | INFRA-CI-CORE |
| [PLY0077](PLY0077.md) | error | 1 (EnvironmentError) | `<reason>` | INFRA-FUZZ |
| [PLY0078](PLY0078.md) | error | 1 (EnvironmentError) | `<file>(<line>,<column>): <reason naming the field>` | INFRA-BENCH |
| [PLY0079](PLY0079.md) | error | 1 (EnvironmentError) | `benchmark '<id>/<variant>': the program exited with <code>; nothing is recorded` | INFRA-BENCH |
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

## PLY0800–PLY0899 · permutation

| Code | Severity | Exit code | Message | Owner |
| --- | --- | --- | --- | --- |
| [PLY0800](PLY0800.md) | error | 3 (UnsupportedConstruct) | `mapping width <width> is outside 1..4096` | PERM-MODEL |
| [PLY0801](PLY0801.md) | error | 3 (UnsupportedConstruct) | `output bit <output> reads input bit <input>, input width is <width>` | PERM-MODEL |
| [PLY0802](PLY0802.md) | error | 3 (UnsupportedConstruct) | `mapping <text\|document> '<input>': <reason>` | PERM-MODEL |
| [PLY0803](PLY0803.md) | error | 3 (UnsupportedConstruct) | `mapping is not a bijection (<reason>); it has no inverse` | PERM-ALGEBRA |
| [PLY0804](PLY0804.md) | error | 3 (UnsupportedConstruct) | `cannot compose: the inner mapping has <inner> output bits, the outer mapping reads <outer> input bits` | PERM-ALGEBRA |
| [PLY0805](PLY0805.md) | error | 3 (UnsupportedConstruct) | `<reason>` | PERM-HIERARCHY |
| [PLY0806](PLY0806.md) | error | 3 (UnsupportedConstruct) | `input <hex> is outside the declared input domain` | PERM-HIERARCHY |
| [PLY0807](PLY0807.md) | error | 3 (UnsupportedConstruct) | `truth table of <inputs> inputs exceeds the limit of 16` | PERM-HIERARCHY |

## PLY0900–PLY0999 · catalog

| Code | Severity | Exit code | Message | Owner |
| --- | --- | --- | --- | --- |
| [PLY0950](PLY0950.md) | error | 8 (LicenseBlocker) | `'<path>' has no licence audit record` | ADP-NCNN-LICENCE |

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
| [PLY1107](PLY1107.md) | error | 3 (UnsupportedConstruct) | `a thrown value that is not an Error reaches <place>, which observes it` | #1323 |
| [PLY1152](PLY1152.md) | error | 3 (UnsupportedConstruct) | `'<operation>' cannot be expressed in JavaScript profile '<profile>'` | PROTOTYPE |

## PLY1500–PLY1599 · rust

| Code | Severity | Exit code | Message | Owner |
| --- | --- | --- | --- | --- |
| [PLY1500](PLY1500.md) | error | 4 (InvalidSource) | `<E####: the error, or the denied lint and its name>` | #466 |
| [PLY1501](PLY1501.md) | error | 3 (UnsupportedConstruct) | `type '<type>' is not supported by the Rust frontend` | #466 |
| [PLY1502](PLY1502.md) | error | 3 (UnsupportedConstruct) | `'<construct>' is not supported by the Rust frontend` | #466 |
| [PLY1503](PLY1503.md) | error | 2 (UsageError) | `the overflow mode cannot be read from Cargo: <the profile and the reason>` | #466 |
| [PLY1504](PLY1504.md) | error | 4 (InvalidSource) | `the type of '<name>' could not be resolved` | #466 |
| [PLY1510](PLY1510.md) | error | 3 (UnsupportedConstruct) | `'<construct>' is not supported by the Rust frontend` | #463 |
| [PLY1511](PLY1511.md) | error | 3 (UnsupportedConstruct) | `pattern '<pattern>' is not supported` | #463 |
| [PLY1512](PLY1512.md) | error | 3 (UnsupportedConstruct) | `iterating over '<iterable>' is not supported; only integer ranges with rev() and step_by() are` | #463 |
| [PLY1513](PLY1513.md) | error | 3 (UnsupportedConstruct) | `'<construct>' requires coroutine lowering, which the Rust frontend does not provide` | #463 |
| [PLY1520](PLY1520.md) | error | 3 (UnsupportedConstruct) | `'<construct>' is not supported by the Rust frontend` | #465 |
| [PLY1521](PLY1521.md) | error | 3 (UnsupportedConstruct) | `text type '<type>' is not supported` | #465 |
| [PLY1522](PLY1522.md) | error | 3 (UnsupportedConstruct) | `unsafe construct '<construct>' is not supported` | #465 |
| [PLY1523](PLY1523.md) | error | 3 (UnsupportedConstruct) | `smart pointer or interior-mutability type '<type>' is not supported` | #465 |
| [PLY1524](PLY1524.md) | error | 3 (UnsupportedConstruct) | `method '<name>' is not supported on slices, arrays or Vec` | #465 |
| [PLY1525](PLY1525.md) | error | 3 (UnsupportedConstruct) | `collection type '<type>' is not supported` | #465 |
| [PLY1530](PLY1530.md) | error | 3 (UnsupportedConstruct) | `function form '<form>' is not supported` | #464 |
| [PLY1531](PLY1531.md) | error | 3 (UnsupportedConstruct) | `macro '<name>!' is not supported` | #464 |
| [PLY1532](PLY1532.md) | error | 3 (UnsupportedConstruct) | `closure escapes its defining function: <how>` | #464 |
| [PLY1533](PLY1533.md) | error | 3 (UnsupportedConstruct) | `attribute '<name>' is not supported` | #464 |
| [PLY1534](PLY1534.md) | error | 3 (UnsupportedConstruct) | `'<path>' refers to an item outside the supported standard-library subset or to another crate` | #464 |
| [PLY1535](PLY1535.md) | error | 3 (UnsupportedConstruct) | `iterator method '<name>' is not supported` | #464 |
| [PLY1536](PLY1536.md) | error | 2 (UsageError) | `the source profile does not set 'debugAssertions': <macro>` | #464 |
| [PLY1540](PLY1540.md) | error | 3 (UnsupportedConstruct) | `traits are not supported by the Rust frontend: '<construct>' (the object-model contract of #473 and #474)` | #467 |
| [PLY1541](PLY1541.md) | error | 3 (UnsupportedConstruct) | `generic parameters are not supported: '<construct>'` | #467 |
| [PLY1542](PLY1542.md) | error | 3 (UnsupportedConstruct) | `lifetime parameters and reference fields are not supported: '<construct>'` | #467 |
| [PLY1543](PLY1543.md) | error | 3 (UnsupportedConstruct) | `enum '<name>' has variants with data` | #467 |
| [PLY1544](PLY1544.md) | error | 3 (UnsupportedConstruct) | `attribute or derive '<name>' is not supported` | #467 |
| [PLY1545](PLY1545.md) | error | 3 (UnsupportedConstruct) | `'<method or type>' is not supported on Option or Result` | #467 |
| [PLY1546](PLY1546.md) | error | 3 (UnsupportedConstruct) | `destructors are not supported: 'impl Drop for <type>'` | #467 |
| [PLY1550](PLY1550.md) | error | 3 (UnsupportedConstruct) | `'<construct>' cannot be expressed in Rust profile '<profile>' (in <place>)` | #461 |
| [PLY1552](PLY1552.md) | error | 3 (UnsupportedConstruct) | `trap policy 'result' is not available for '<construct>' (in <place>)` | #1392 |
| [PLY1558](PLY1558.md) | error | 3 (UnsupportedConstruct) | `'<construct>' cannot be written as a Rust iterator (in <place>)` | #1391 |
| [PLY1560](PLY1560.md) | error | 3 (UnsupportedConstruct) | `exception region in <function> cannot be emitted under the trap policy '<panic\|result>': '<construct>' is not supported by the Rust backend` | #458 |
| [PLY1566](PLY1566.md) | error | 3 (UnsupportedConstruct) | `views '<first>' and '<second>' may alias and '<second>' is written in <place>; safe Rust cannot express this - provide a no-alias fact or select a copying target profile` | #460 |
| [PLY1567](PLY1567.md) | error | 3 (UnsupportedConstruct) | `mutable overlay view in '<function>' requires unsafe Rust; the target profile sets allowUnsafe to false` | #460 |
| [PLY1574](PLY1574.md) | error | 3 (UnsupportedConstruct) | `exported names '<first>' and '<second>' both map to '<rust name>'` | #459 |
| [PLY1584](PLY1584.md) | error | 3 (UnsupportedConstruct) | `layout of '<struct>' (<explicit field offsets\|overlapping fields, an untagged union\|packed to <n> bytes\|a size of <n> bytes>) has no safe Rust representation` | #462 |
| [PLY1591](PLY1591.md) | error | 6 (TargetBuildFailure) | `the Rust compiler rejected the input: <every error with its code, text and location>` | #468 |
| [PLY1592](PLY1592.md) | error | 7 (OracleFailure) | `the Rust driver died with exit code <code>: <standard error>` | #468 |
| [PLY1595](PLY1595.md) | error | 7 (OracleFailure) | `<reason>` | #468 |

## PLY1800–PLY1899 · python

| Code | Severity | Exit code | Message | Owner |
| --- | --- | --- | --- | --- |
| [PLY1800](PLY1800.md) | error | 4 (InvalidSource) | `<python's exception class>: <python's message>` | #452 |
| [PLY1801](PLY1801.md) | error | 3 (UnsupportedConstruct) | `<reason>` | #452 |
| [PLY1802](PLY1802.md) | error | 3 (UnsupportedConstruct) | `'<construct>' is not supported by the Python frontend` | #452 |
| [PLY1803](PLY1803.md) | error | 3 (UnsupportedConstruct) | `<reason>` | #452 |
| [PLY1804](PLY1804.md) | error | 3 (UnsupportedConstruct) | `the integer '<literal>' needs more than 64 bits, which the import does not hold yet` | #452 |
| [PLY1810](PLY1810.md) | error | 3 (UnsupportedConstruct) | `statement '<statement>' is not supported by the Python frontend` | #449 |
| [PLY1811](PLY1811.md) | error | 3 (UnsupportedConstruct) | `'<name>' may be unbound here; python would raise UnboundLocalError` | #449 |
| [PLY1812](PLY1812.md) | error | 3 (UnsupportedConstruct) | `match pattern '<pattern>' is not supported; supported are literals, '\|', captures, '_' and guards` | #449 |
| [PLY1813](PLY1813.md) | error | 3 (UnsupportedConstruct) | `iteration over '<iterable>' is not supported` | #449 |
| [PLY1820](PLY1820.md) | error | 3 (UnsupportedConstruct) | `sequence operation '<operation>' is not supported by the Python frontend` | #451 |
| [PLY1821](PLY1821.md) | error | 3 (UnsupportedConstruct) | `slice form '<form>' is not supported; slices are imported as values only` | #451 |
| [PLY1822](PLY1822.md) | error | 3 (UnsupportedConstruct) | `string operation '<operation>' is not supported by the Python frontend` | #451 |
| [PLY1823](PLY1823.md) | error | 3 (UnsupportedConstruct) | `struct format or array typecode '<format>' is not supported: <reason>` | #451 |
| [PLY1830](PLY1830.md) | error | 3 (UnsupportedConstruct) | `'<feature>' is not supported for functions by the Python frontend` | #450 |
| [PLY1832](PLY1832.md) | error | 3 (UnsupportedConstruct) | `the function value '<name>' escapes, or '<name>' is a generator or coroutine; only direct calls, nested functions called by name and lambdas passed to the listed built-ins are imported` | #450 |
| [PLY1833](PLY1833.md) | error | 3 (UnsupportedConstruct) | `'<name>' is neither part of this import nor in the Python call map` | #450 |
| [PLY1834](PLY1834.md) | error | 3 (UnsupportedConstruct) | `the default of parameter '<name>' is a mutable object shared across calls` | #450 |
| [PLY1840](PLY1840.md) | error | 3 (UnsupportedConstruct) | `'<feature>' is outside the first Python subset (dataclasses, simple final classes, enums); it is imported by <issue>` | #453 |
| [PLY1841](PLY1841.md) | error | 3 (UnsupportedConstruct) | `generic declaration '<declaration>' is not supported; generic classes and functions are imported by #472` | #453 |
| [PLY1842](PLY1842.md) | error | 3 (UnsupportedConstruct) | `dict or set operation '<operation>' is not supported by the Python frontend` | #453 |
| [PLY1843](PLY1843.md) | error | 3 (UnsupportedConstruct) | `exception construct '<construct>' is not supported by the Python frontend` | #453 |
| [PLY1844](PLY1844.md) | error | 3 (UnsupportedConstruct) | `attribute '<name>' of class '<class>' is created dynamically; only attributes assigned in __init__ or declared as dataclass fields are imported` | #453 |
| [PLY1850](PLY1850.md) | error | 3 (UnsupportedConstruct) | `type '<type>' has no Python representation in profile '<profile>' (function '<function>')` | BE-PY-SCALARS |
| [PLY1851](PLY1851.md) | error | 3 (UnsupportedConstruct) | `'<construct>' cannot be expressed in Python profile '<profile>' (function '<function>')` | BE-PY-SCALARS |
| [PLY1858](PLY1858.md) | error | 3 (UnsupportedConstruct) | `control construct '<construct>' has no Python form (function '<function>')` | BE-PY-CONTROL |
| [PLY1874](PLY1874.md) | error | 3 (UnsupportedConstruct) | `exports '<a>' and '<b>' both map to the Python name '<name>'; or: export '<a>' has no valid Python name` | BE-PY-FUNCTIONS |

## PLY3900–PLY3999 · tokenized-basic

| Code | Severity | Exit code | Message | Owner |
| --- | --- | --- | --- | --- |
| [PLY3935](PLY3935.md) | error | 7 (OracleFailure) | `VICE run of '<program>' failed: <cause>` | FMT-CBMBASIC-ORACLE |
| [PLY3936](PLY3936.md) | error | 7 (OracleFailure) | `petcat <operation> failed for dialect '<dialect>': <cause>` | FMT-CBMBASIC-ORACLE |
| [PLY3960](PLY3960.md) | error | 7 (OracleFailure) | `PC-BASIC <operation> failed: <cause>` | FMT-GWBASIC-ORACLE |
