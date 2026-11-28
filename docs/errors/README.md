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
| [PLY0016](PLY0016.md) | error | 1 (EnvironmentError) | `no manifest in '<path>'` | CLI-VERIFY |
| [PLY0017](PLY0017.md) | error | 2 (UsageError) | `'<path>' holds no test vectors: <reason>` | CLI-VERIFY |
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
| [PLY0055](PLY0055.md) | warning | — | `proof of <rule> inconclusive (<reason>); rewrite not applied` | VER-SMT |
| [PLY0056](PLY0056.md) | error | 5 (SemanticMismatch) | `proof of <rule> refuted[ at width <w>]: <name>=<hex>[, ...]: before <hex>, after <hex>` | VER-SMT |
| [PLY0057](PLY0057.md) | error | 10 (BudgetExceeded) | `exhaustive proof of <rule> needs <n> input bits, budget is <m>` | VER-EXHAUSTIVE |
| [PLY0058](PLY0058.md) | error | 1 (EnvironmentError) | `gate record '<file>': fixture '<fixture>' claims '<state>': <reason>` | VER-GATES |
| [PLY0059](PLY0059.md) (reserved) | error | 5 (SemanticMismatch) | `round trip '<id>' leg <leg> (<opt>): <function>(<arguments>) expected <expected>, got <actual>` | VER-ROUNDTRIP |
| [PLY0060](PLY0060.md) | error | 1 (EnvironmentError) | `evidence bundle '<directory>' <reason>` | VER-EVIDENCE |
| [PLY0061](PLY0061.md) | error | 1 (EnvironmentError) | `<reason>` | SPEC-CAPABILITIES |
| [PLY0062](PLY0062.md) | error | 3 (UnsupportedConstruct) | `proof of <rule>: operation '<operation>' is not modelled by prover '<prover>'` | VER-SMT |
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
| [PLY0115](PLY0115.md) | error | 70 (InternalError) | `instruction <number> (<kind>) in '@<function>' has no origin and is not marked synthetic` | IR-SOURCEMAP |
| [PLY0116](PLY0116.md) | error | 4 (InvalidSource) | `malformed location '<text>'` | IR-SOURCEMAP |
| [PLY0117](PLY0117.md) | error | 4 (InvalidSource) | `location refers to source <index>, but the module declares <count> sources` | IR-SOURCEMAP |

## PLY0200–PLY0299 · types

| Code | Severity | Exit code | Message | Owner |
| --- | --- | --- | --- | --- |
| [PLY0250](PLY0250.md) | error | 70 (InternalError) | `unknown intrinsic '<name>'` | INT-REGISTRY |
| [PLY0251](PLY0251.md) | error | 70 (InternalError) | `intrinsic contract '<file>': <finding>` | INT-REGISTRY |
| [PLY0252](PLY0252.md) | error | 70 (InternalError) | `intrinsic '<name>' does not admit <type> as <position>` | INT-REGISTRY |
| [PLY0253](PLY0253.md) | error | 70 (InternalError) | `intrinsic '<name>' is registered twice` | INT-REGISTRY |
| [PLY0254](PLY0254.md) | error | 70 (InternalError) | `intrinsic '<name>' has no fallback` | INT-REGISTRY |
| [PLY0255](PLY0255.md) | error | 70 (InternalError) | `intrinsic '<name>': attribute '<attribute>' = <value> is outside [<low>, <high>]` | INT-REGISTRY |
| [PLY0256](PLY0256.md) | error | 70 (InternalError) | `intrinsic '<name>' lowers to itself through <path>` | INT-REGISTRY |

## PLY0300–PLY0399 · optimization

| Code | Severity | Exit code | Message | Owner |
| --- | --- | --- | --- | --- |
| [PLY0300](PLY0300.md) | error | 70 (InternalError) | `no analysis provides facts of domain '<domain>'` | ANA-FACADE |
| [PLY0301](PLY0301.md) | info | — | `analysis '<domain>' stopped at its budget in '<function>'; affected facts are Unknown` | ANA-FACADE |
| [PLY0302](PLY0302.md) | error | 70 (InternalError) | `unsound fact: <domain> claims <fact> for <value> at <point>, observed <observed>` | ANA-FACADE |
| [PLY0355](PLY0355.md) | error | 3 (UnsupportedConstruct) | `target profile '<language>/<profile>' has no cost profile` | OPT-COST |
| [PLY0356](PLY0356.md) | error | 4 (InvalidSource) | `cost profile '<path>': <key>: <problem>` | OPT-COST |

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
| [PLY0810](PLY0810.md) | error | 3 (UnsupportedConstruct) | `network step <n> (<op>): <problem>` | PERM-NETWORK |
| [PLY0811](PLY0811.md) | error | 4 (InvalidSource) | `primitive '<primitive>' is not available in cost table '<table>'` | PERM-NETWORK |
| [PLY0812](PLY0812.md) | error | 4 (InvalidSource) | `primitive '<primitive>' does not support width <width>` | PERM-NETWORK |

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

## PLY1400–PLY1499 · cpp

| Code | Severity | Exit code | Message | Owner |
| --- | --- | --- | --- | --- |
| [PLY1400](PLY1400.md) | error | 4 (InvalidSource) | `C++ syntax error: <detail>` | #400 |
| [PLY1401](PLY1401.md) | error | 3 (UnsupportedConstruct) | `C++ construct '<construct>' is not supported by the C++ frontend` | #400 |
| [PLY1402](PLY1402.md) | error | 3 (UnsupportedConstruct) | `C++ type '<type>' is not supported by the C++ frontend` | #400 |
| [PLY1403](PLY1403.md) | error | 3 (UnsupportedConstruct) | `C++ call '<call>' is not supported by the C++ frontend` | #400 |
| [PLY1404](PLY1404.md) | error | 4 (InvalidSource) | `unresolved C++ symbol '<symbol>'` | #400 |
| [PLY1450](PLY1450.md) | error | 3 (UnsupportedConstruct) | `construct '<construct>' cannot be emitted as C++` | #399 |
| [PLY1451](PLY1451.md) | error | 3 (UnsupportedConstruct) | `type '<type>' has no C++ representation in profile '<profile>'` | #399 |
| [PLY1452](PLY1452.md) | error | 3 (UnsupportedConstruct) | `intrinsic '<intrinsic>' cannot be emitted as C++` | #399 |

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

## PLY1600–PLY1699 · kotlin

| Code | Severity | Exit code | Message | Owner |
| --- | --- | --- | --- | --- |
| [PLY1600](PLY1600.md) | error | 4 (InvalidSource) | `Kotlin syntax error: <detail>` | #424 |
| [PLY1601](PLY1601.md) | error | 3 (UnsupportedConstruct) | `<the operator, conversion or literal and why>` | #424 |
| [PLY1610](PLY1610.md) | error | 3 (UnsupportedConstruct) | `<the construct and why>` | #421 |
| [PLY1620](PLY1620.md) | error | 3 (UnsupportedConstruct) | `<the construct and why>` | #423 |
| [PLY1630](PLY1630.md) | error | 3 (UnsupportedConstruct) | `<the function or name and why>` | #422 |
| [PLY1640](PLY1640.md) | error | 3 (UnsupportedConstruct) | `<the class, interface, object or nullable type>` | #425 |
| [PLY1650](PLY1650.md) | error | 3 (UnsupportedConstruct) | `type '<type>' has no Kotlin representation in profile '<profile>'` | #419 |
| [PLY1651](PLY1651.md) | error | 3 (UnsupportedConstruct) | `'<operation>' cannot be expressed in Kotlin profile '<profile>'` | #419 |
| [PLY1658](PLY1658.md) | error | 3 (UnsupportedConstruct) | `<the statement or jump and why>` | #416 |
| [PLY1666](PLY1666.md) | error | 3 (UnsupportedConstruct) | `<the layout, view or pointer and why>` | #418 |
| [PLY1674](PLY1674.md) | error | 3 (UnsupportedConstruct) | `exports '<a>' and '<b>' both map to the Kotlin name '<name>'` | #417 |
| [PLY1682](PLY1682.md) | error | 3 (UnsupportedConstruct) | `<the class, interface or struct and why>` | #420 |
| [PLY1691](PLY1691.md) | error | 6 (TargetBuildFailure) | `the Kotlin toolchain rejected the input: <the compiler's errors with file and line>` | #426 |
| [PLY1692](PLY1692.md) | error | 7 (OracleFailure) | `the Kotlin driver died with exit code <code>: <standard error>` | #426 |
| [PLY1694](PLY1694.md) | error | 7 (OracleFailure) | `call <call>: expected <expected>, actual <actual>` | #426 |
| [PLY1695](PLY1695.md) | error | 7 (OracleFailure) | `<reason>` | #426 |

## PLY1700–PLY1799 · perl

| Code | Severity | Exit code | Message | Owner |
| --- | --- | --- | --- | --- |
| [PLY1700](PLY1700.md) | error | 4 (InvalidSource) | `Perl <detail>` | #1561 |
| [PLY1701](PLY1701.md) | error | 3 (UnsupportedConstruct) | `Perl construct outside the parser: <construct>` | #1561 |
| [PLY1702](PLY1702.md) | error | 3 (UnsupportedConstruct) | `'<construct>' is not supported by the Perl frontend` | #438 |
| [PLY1710](PLY1710.md) | error | 3 (UnsupportedConstruct) | `'<construct>' is not supported by the Perl frontend` | #435 |
| [PLY1711](PLY1711.md) | error | 3 (UnsupportedConstruct) | `'<construct>' does not target a loop that lexically encloses it in the same sub; dynamic loop control is not imported` | #435 |
| [PLY1720](PLY1720.md) | error | 3 (UnsupportedConstruct) | `array or list operation '<construct>' is not supported by the Perl frontend` | #437 |
| [PLY1721](PLY1721.md) | error | 3 (UnsupportedConstruct) | `reference construct '<construct>' is not supported by the Perl frontend` | #437 |
| [PLY1722](PLY1722.md) | error | 3 (UnsupportedConstruct) | `string operation '<construct>' is not supported; the Perl subset handles byte strings only` | #437 |
| [PLY1723](PLY1723.md) | error | 3 (UnsupportedConstruct) | `pack/unpack template '<template>' is not supported: <reason>` | #437 |
| [PLY1730](PLY1730.md) | error | 3 (UnsupportedConstruct) | `'<construct>' is not supported for subs and modules by the Perl frontend` | #436 |
| [PLY1732](PLY1732.md) | error | 3 (UnsupportedConstruct) | `the code reference '<name>' escapes; only anonymous subs called through the lexical they are assigned to are imported` | #436 |
| [PLY1733](PLY1733.md) | error | 3 (UnsupportedConstruct) | `'<name>' is neither part of this import nor in the Perl call map` | #436 |
| [PLY1740](PLY1740.md) | error | 3 (UnsupportedConstruct) | `'<construct>' uses Perl's object system, which is outside the first Perl subset` | #439 |
| [PLY1741](PLY1741.md) | error | 3 (UnsupportedConstruct) | `'<construct>' observes the order of a hash, which Perl leaves unspecified; sort the keys` | #439 |
| [PLY1742](PLY1742.md) | error | 3 (UnsupportedConstruct) | `error-handling construct '<construct>' is not supported by the Perl frontend` | #439 |
| [PLY1743](PLY1743.md) | error | 3 (UnsupportedConstruct) | `'<construct>' relies on tie, typeglob or special-variable behaviour that is not imported` | #439 |
| [PLY1750](PLY1750.md) | error | 3 (UnsupportedConstruct) | `type '<type>' has no Perl representation in profile '<profile>'` | #433 |
| [PLY1751](PLY1751.md) | error | 3 (UnsupportedConstruct) | `operation '<operation>' cannot be emitted as Perl` | #433 |
| [PLY1758](PLY1758.md) | error | 3 (UnsupportedConstruct) | `control construct '<construct>' has no Perl form` | #430 |
| [PLY1766](PLY1766.md) | error | 3 (UnsupportedConstruct) | `pointer '<construct>' cannot be represented in Perl` | #432 |
| [PLY1767](PLY1767.md) | error | 3 (UnsupportedConstruct) | `a live view of '<source>' as '<target>' is not supported by the Perl backend; use explicit loads and stores` | #432 |
| [PLY1774](PLY1774.md) | error | 3 (UnsupportedConstruct) | `<reason>` | #431 |
| [PLY1775](PLY1775.md) | error | 3 (UnsupportedConstruct) | `the signature of export '<export>' uses '<construct>', which the Perl backend cannot expose` | #431 |
| [PLY1782](PLY1782.md) | error | 3 (UnsupportedConstruct) | `'<construct>' is beyond the Perl backend's plain-Perl subset (hash records, constants, undef, die/eval)` | #434 |
| [PLY1783](PLY1783.md) | error | 3 (UnsupportedConstruct) | `trap kind '<kind>' has no Perl mapping` | #434 |

## PLY1800–PLY1899 · python

| Code | Severity | Exit code | Message | Owner |
| --- | --- | --- | --- | --- |
| [PLY1800](PLY1800.md) | error | 4 (InvalidSource) | `<CPython's exception class>: <CPython's message>` | #452 |
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

## PLY2100–PLY2199 · go

| Code | Severity | Exit code | Message | Owner |
| --- | --- | --- | --- | --- |
| [PLY2100](PLY2100.md) | error | 4 (InvalidSource) | `Go <kind> error: <message>` | #697 |
| [PLY2101](PLY2101.md) | error | 3 (UnsupportedConstruct) | `type '<type>' is not supported by the Go frontend` | #697 |
| [PLY2102](PLY2102.md) | error | 3 (UnsupportedConstruct) | `'<construct>' is not supported by the Go frontend` | #697 |
| [PLY2150](PLY2150.md) | error | 3 (UnsupportedConstruct) | `type '<type>' has no Go representation in profile '<profile>' (function '<function>')` | #692 |
| [PLY2151](PLY2151.md) | error | 3 (UnsupportedConstruct) | `operation '<operation>' cannot be emitted as Go in profile '<profile>' (function '<function>')` | #692 |
| [PLY2160](PLY2160.md) | error | 3 (UnsupportedConstruct) | `control-flow construct '<construct>' cannot be emitted as Go (function '<function>')` | #689 |
| [PLY2170](PLY2170.md) | error | 3 (UnsupportedConstruct) | `function feature '<feature>' cannot be emitted as Go` | #690 |
| [PLY2172](PLY2172.md) | error | 3 (UnsupportedConstruct) | `functions '<first>' and '<second>' collide as '<name>'` | #690 |
| [PLY2173](PLY2173.md) | error | 3 (UnsupportedConstruct) | `profile setting '<setting>' is missing or invalid: <reason>` | #690 |

## PLY2600–PLY2699 · visualbasic

| Code | Severity | Exit code | Message | Owner |
| --- | --- | --- | --- | --- |
| [PLY2600](PLY2600.md) | error | 4 (InvalidSource) | `VBScript syntax error: <detail>` | #1133 |
| [PLY2601](PLY2601.md) | error | 3 (UnsupportedConstruct) | `VBScript construct '<construct>' is not supported` | #1133 |
| [PLY2602](PLY2602.md) | error | 3 (UnsupportedConstruct) | `VBScript call '<call>' is not supported` | #1133 |
| [PLY2603](PLY2603.md) | error | 4 (InvalidSource) | `unresolved VBScript symbol '<symbol>'` | #1133 |
| [PLY2604](PLY2604.md) | error | 4 (InvalidSource) | `invalid VBScript contract: <detail>` | #1133 |
| [PLY2610](PLY2610.md) | error | 4 (InvalidSource) | `<BC id>: <the message of the Visual Basic compiler>` | #1083 |
| [PLY2611](PLY2611.md) | error | 3 (UnsupportedConstruct) | `'<construct>' is not supported by the Visual Basic .NET frontend` | #1083 |
| [PLY2645](PLY2645.md) | error | 4 (InvalidSource) | `the classic Visual Basic parser reports: line <n>: <detail>` | #1090 |
| [PLY2646](PLY2646.md) | error | 3 (UnsupportedConstruct) | `'<construct>' is not supported by the classic Visual Basic profile` | #1090 |
| [PLY2648](PLY2648.md) | error | 4 (InvalidSource) | `<what is not declared, or the operation and the type that do not fit>` | #1090 |
| [PLY2650](PLY2650.md) | error | 3 (UnsupportedConstruct) | `construct '<construct>' cannot be emitted as VBScript` | #1133 |
| [PLY2651](PLY2651.md) | error | 3 (UnsupportedConstruct) | `type '<type>' has no VBScript representation in profile '<profile>'` | #1133 |
| [PLY2652](PLY2652.md) | error | 3 (UnsupportedConstruct) | `intrinsic '<intrinsic>' cannot be emitted as VBScript` | #1133 |
| [PLY2655](PLY2655.md) | error | 3 (UnsupportedConstruct) | `'<construct>' has no form in Visual Basic profile '<profile>'` | #1085 |
| [PLY2656](PLY2656.md) | error | 3 (UnsupportedConstruct) | `type '<type>' has no Visual Basic representation in profile '<profile>'` | #1088 |
| [PLY2657](PLY2657.md) | error | 3 (UnsupportedConstruct) | `'<operation>' cannot be expressed in Visual Basic profile '<profile>'` | #1088 |
| [PLY2658](PLY2658.md) | error | 3 (UnsupportedConstruct) | `exports '<first>' and '<second>' both map to the Visual Basic name '<name>'` | #1086 |
| [PLY2690](PLY2690.md) | error | 6 (TargetBuildFailure) | `the Visual Basic compiler of dotnet-sdk rejected the project: <what the build printed>` | #1096 |
| [PLY2691](PLY2691.md) | error | 6 (TargetBuildFailure) | `LibreOffice Basic did not answer (<outcome>): a module it cannot compile or an uncaught run-time error stops it at a dialog` | #1096 |
| [PLY2692](PLY2692.md) | error | 7 (OracleFailure) | `the LibreOffice Basic driver exited with code <code>: <standard error>` | #1096 |
| [PLY2693](PLY2693.md) | error | 7 (OracleFailure) | `the Visual Basic .NET driver exited with code <code> without its answer: <standard error>` | #1096 |
| [PLY2695](PLY2695.md) | error | 7 (OracleFailure) | `<the driver answered another number of calls, or a line that is no answer>` | #1096 |
| [PLY2696](PLY2696.md) | error | 7 (OracleFailure) | `the recorded dialect table differs from spec/languages/visualbasic/vbnet-errors.yaml at '<entry>'` | #1096 |

## PLY2800–PLY2899 · batch

| Code | Severity | Exit code | Message | Owner |
| --- | --- | --- | --- | --- |
| [PLY2800](PLY2800.md) | error | 4 (InvalidSource) | `invalid Batch script: <detail>` | #1455 |
| [PLY2802](PLY2802.md) | error | 3 (UnsupportedConstruct) | `'<construct>' is not supported by the Batch frontend` | #1455 |
| [PLY2850](PLY2850.md) | error | 3 (UnsupportedConstruct) | `'<construct>' cannot be emitted for the cmd Batch profile` | #1456 |
| [PLY2851](PLY2851.md) | error | 3 (UnsupportedConstruct) | `'<construct>' cannot be emitted for the dos622 Batch profile` | #1467 |

## PLY3000–PLY3099 · msil

| Code | Severity | Exit code | Message | Owner |
| --- | --- | --- | --- | --- |
| [PLY3000](PLY3000.md) | error | 4 (InvalidSource) | `<detail>` | #833 |
| [PLY3001](PLY3001.md) | error | 4 (InvalidSource) | `<detail>` | #833 |
| [PLY3002](PLY3002.md) | error | 4 (InvalidSource) | `<detail>` | #833 |
| [PLY3003](PLY3003.md) | error | 3 (UnsupportedConstruct) | `<detail>` | #833 |
| [PLY3004](PLY3004.md) | error | 3 (UnsupportedConstruct) | `<detail>` | #833 |
| [PLY3005](PLY3005.md) | error | 3 (UnsupportedConstruct) | `<detail>` | #833 |
| [PLY3006](PLY3006.md) | error | 3 (UnsupportedConstruct) | `<detail>` | #833 |
| [PLY3007](PLY3007.md) | error | 4 (InvalidSource) | `<detail>` | #833 |
| [PLY3008](PLY3008.md) | error | 3 (UnsupportedConstruct) | `<detail>` | #833 |
| [PLY3050](PLY3050.md) | error | 3 (UnsupportedConstruct) | `construct '<construct>' is not supported by the MSIL backend` | #833 |
| [PLY3051](PLY3051.md) | error | 3 (UnsupportedConstruct) | `type '<type>' is not supported by the MSIL backend` | #833 |
| [PLY3052](PLY3052.md) | error | 3 (UnsupportedConstruct) | `intrinsic '<intrinsic>' is not supported by the MSIL backend` | #833 |

## PLY3100–PLY3199 · jvm

| Code | Severity | Exit code | Message | Owner |
| --- | --- | --- | --- | --- |
| [PLY3100](PLY3100.md) | error | 4 (InvalidSource) | `<detail>` | #831 |
| [PLY3101](PLY3101.md) | error | 4 (InvalidSource) | `<detail>` | #831 |
| [PLY3102](PLY3102.md) | error | 3 (UnsupportedConstruct) | `<detail>` | #831 |
| [PLY3103](PLY3103.md) | error | 3 (UnsupportedConstruct) | `<detail>` | #831 |
| [PLY3104](PLY3104.md) | error | 3 (UnsupportedConstruct) | `<detail>` | #831 |
| [PLY3105](PLY3105.md) | error | 3 (UnsupportedConstruct) | `<detail>` | #831 |
| [PLY3106](PLY3106.md) | error | 4 (InvalidSource) | `<detail>` | #831 |
| [PLY3107](PLY3107.md) | error | 3 (UnsupportedConstruct) | `<detail>` | #831 |
| [PLY3108](PLY3108.md) | error | 3 (UnsupportedConstruct) | `<detail>` | #831 |
| [PLY3150](PLY3150.md) | error | 3 (UnsupportedConstruct) | `<detail>` | #831 |
| [PLY3151](PLY3151.md) | error | 3 (UnsupportedConstruct) | `<detail>` | #831 |
| [PLY3152](PLY3152.md) | error | 3 (UnsupportedConstruct) | `<detail>` | #831 |

## PLY3900–PLY3999 · tokenized-basic

| Code | Severity | Exit code | Message | Owner |
| --- | --- | --- | --- | --- |
| [PLY3935](PLY3935.md) | error | 7 (OracleFailure) | `VICE run of '<program>' failed: <cause>` | FMT-CBMBASIC-ORACLE |
| [PLY3936](PLY3936.md) | error | 7 (OracleFailure) | `petcat <operation> failed for dialect '<dialect>': <cause>` | FMT-CBMBASIC-ORACLE |
| [PLY3960](PLY3960.md) | error | 7 (OracleFailure) | `PC-BASIC <operation> failed: <cause>` | FMT-GWBASIC-ORACLE |

## PLY4000–PLY4099 · machine

| Code | Severity | Exit code | Message | Owner |
| --- | --- | --- | --- | --- |
| [PLY4000](PLY4000.md) | error | 4 (InvalidSource) | `'<name>' is not a register or flag of machine profile '<profile>'` | MACH-IR |
| [PLY4001](PLY4001.md) | error | 4 (InvalidSource) | `'<instruction>' has type <type> but register '<register>' is <bits> bits wide` | MACH-IR |
| [PLY4002](PLY4002.md) | error | 4 (InvalidSource) | `flag '<flag>' is read at <where> but is undefined after <undefined at>` | MACH-IR |
| [PLY4003](PLY4003.md) | error | 4 (InvalidSource) | `'@<function>' uses machine operations but module '<module>' declares no machine profile; or: machine profile '<profile>': <reason>` | MACH-IR |
| [PLY4004](PLY4004.md) | error | 4 (InvalidSource) | `'<instruction>' is not allowed in '@<function>' after normalization to root registers` | MACH-IR |
| [PLY4005](PLY4005.md) | error | 4 (InvalidSource) | `'@<function>' is not well-formed MachineIR: <problem>` | MACH-IR |
| [PLY4006](PLY4006.md) | error | 4 (InvalidSource) | `MachineIR text is not valid: <problem>` | MACH-IR |
| [PLY4009](PLY4009.md) | error | 4 (InvalidSource) | `address ranges '<a>' and '<b>' overlap at <address>` | MACH-REGIONS |
| [PLY4010](PLY4010.md) | warning | — | `'<instruction>' at <where> accesses constant address <address>, which no declared region covers; treated as unknown memory` | MACH-REGIONS |
| [PLY4011](PLY4011.md) | error | 4 (InvalidSource) | `address range '<name>' ends before it starts; or: address range '<name>' exceeds the <bits>-bit address space of '<isa>'` | MACH-REGIONS |
| [PLY4025](PLY4025.md) | error | 4 (InvalidSource) | `interrupt vector <vector> is outside 0..<last> for machine profile '<profile>'; or: trap kind '<kind>' requires a vector` | MACH-TRAP |
| [PLY4026](PLY4026.md) | warning | — | `software interrupt <vector> at <where> has no service contract; all registers and unknown memory are assumed read and written` | MACH-TRAP |
| [PLY4030](PLY4030.md) | error | 1 (EnvironmentError) | `machine vector file '<file>' is not valid: <reason>` | MACH-TRACE |
| [PLY4031](PLY4031.md) | error | 5 (SemanticMismatch) | `'<fixture>' vector '<vector>': <what> expected <expected>, actual <actual>` | MACH-TRACE |
| [PLY4032](PLY4032.md) | error | 1 (EnvironmentError) | `'<file>' observes '<name>', which machine profile '<profile>' does not define` | MACH-TRACE |
| [PLY4033](PLY4033.md) | error | 1 (EnvironmentError) | `'<fixture>' vector '<vector>': the reference produced <count> event(s) of kind '<kind>' but the projection does not observe that kind` | MACH-TRACE |
| [PLY4034](PLY4034.md) | error | 3 (UnsupportedConstruct) | `opaque instruction <id> at <where> cannot be interpreted; or: <kind> trap <id> at <where> resumes but no trap model handles it` | MACH-INTERP |
| [PLY4035](PLY4035.md) | error | 5 (SemanticMismatch) | `undefined flag '<flag>' is read at <where>; or: uninitialized register '<register>' is read at <where>; or: unwritten memory at <address> is read at <where>` | MACH-INTERP |
| [PLY4036](PLY4036.md) (reserved) | error | 5 (SemanticMismatch) | `device model has no value for a <bits>-bit read of <space>:<address>` | MACH-INTERP |
| [PLY4037](PLY4037.md) (reserved) | error | 3 (UnsupportedConstruct) | `code write at <address> overlaps the lifted instruction at <instruction>` | MACH-INTERP |
| [PLY4038](PLY4038.md) | error | 5 (SemanticMismatch) | `indirect jump at <where> went to <address>, which is not among its listed targets; or: indirect jump at <where> has unresolved targets` | MACH-INTERP |
| [PLY4039](PLY4039.md) | error | 10 (BudgetExceeded) | `the interpretation of '@<function>' exceeded its budget of <steps> steps` | MACH-INTERP |
| [PLY4055](PLY4055.md) | error | 1 (EnvironmentError) | `binary manifest '<file>' is not valid: <reason>` | MACH-BIN-MANIFEST |
| [PLY4056](PLY4056.md) | error | 1 (EnvironmentError) | `'<image>' has SHA-256 <actual>, the manifest expects <expected>` | MACH-BIN-MANIFEST |
| [PLY4057](PLY4057.md) | error | 1 (EnvironmentError) | `manifest states <field> '<value>' but <other> states '<other value>'` | MACH-BIN-MANIFEST |
| [PLY4058](PLY4058.md) | error | 1 (EnvironmentError) | `entry '<name>' at <address> is not inside executable code; or: range <start>..<end> lies outside the image` | MACH-BIN-MANIFEST |
| [PLY4059](PLY4059.md) | error | 1 (EnvironmentError) | `'<file>' is not a recognized container; give the manifest field 'container' or use a raw profile` | MACH-BIN-MANIFEST |
| [PLY4060](PLY4060.md) | error | 1 (EnvironmentError) | `'<file>' is not a complete ELF file: <detail>` | MACH-BIN-ELF |
| [PLY4061](PLY4061.md) | error | 3 (UnsupportedConstruct) | `ELF <field> '<value>' is not supported` | MACH-BIN-ELF |
| [PLY4062](PLY4062.md) | error | 1 (EnvironmentError) | `ELF structure is corrupt: <detail>` | MACH-BIN-ELF |
| [PLY4063](PLY4063.md) | warning | — | `relocation type <type> at <address> is not supported; code that depends on it cannot be translated standalone` | MACH-BIN-ELF |
