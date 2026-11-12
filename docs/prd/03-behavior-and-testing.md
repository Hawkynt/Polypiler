# PRD part 03 — Behavior and testing

**Status:** original PRD requirements; planned capabilities are not implementation claims.
Original section numbering is retained.

[PRD index](../PRD.md) · [Previous: Core architecture](02-core-architecture.md) · [Next: Roadmap and delivery](04-roadmap-and-delivery.md)

<!-- PRD content begins -->
<a id="s6"></a>
## 6. BDD — executable behavior specifications

BDD scenarios reside in `tests/features/**/*.feature` (Gherkin or equivalent machine-readable scenarios), mapped to NUnit integration fixtures and real compiler/toolchain runners. Each story below is a required functional contract, **not** documentation-only prose.

<a id="s6-1"></a>
### 6.1 CLI and supported features

```gherkin
Feature: Translate a real algorithm into a target-native library
  Scenario: JavaScript Cipher to C#
    Given an approved Cipher JavaScript algorithm with known-answer vectors
    And the JavaScript and C# semantic profiles are selected
    When I translate the algorithm to C# with --verify
    Then the CLI generates compiling C# source and NUnit tests
    And the generated library passes all applicable reference vectors
    And the manifest identifies source commit, source file, target profile and helpers
    And no JavaScript interpreter is required at runtime

  Scenario: Clearly reject unsupported source semantics
    Given a source function with a runtime feature unsupported by the target profile
    When I translate it in strict mode
    Then the command fails with a stable diagnostic code and source location
    And no artifact is labeled as verified
```

<a id="s6-2"></a>
### 6.2 Source-language semantics and specialization

```gherkin
Feature: Remove unnecessary dynamic values without changing behavior
  Scenario: Proven homogeneous JavaScript byte data
    Given all callers pass a typed byte sequence
    When Polypiler.Core analyzes a bitwise transform
    Then it infers the source ToInt32 conversions and exact result range
    And it may generate a typed byte loop or a verified vectorized implementation
    And the output matches edge-case source values

  Scenario: JS array has a getter with side effects
    Given element access has observable side effects
    When Polypiler.Core considers loop fusion
    Then it preserves access order and count
    And it does not replace the getter with an unguarded direct buffer load

  Scenario: Python bigint exceeds 64 bits
    Given a Python integer operation can exceed 64 bits
    When generating a Rust implementation
    Then arbitrary precision is preserved or translation fails explicitly
    And no silent truncation occurs

  Scenario: Perl list context differs from scalar context
    Given a Perl operation produces context-dependent results
    When generating C# code
    Then the requested context is represented explicitly
    And context-specific expected results are preserved
```

<a id="s6-3"></a>
### 6.3 Proof-driven optimization

```gherkin
Feature: Eliminate bounds checks only when proven safe
  Scenario: Destination is guarded and loop bounds are known
    Given source length is N and destination length is proven at least N
    When a loop accesses both buffers with index 0 through N-1
    Then the optimized IR carries a reusable bounds proof
    And the backend may emit unchecked pointer/array accesses where legal
    And emitted code remains memory safe under the target's lifetime rules

  Scenario: An unknown caller may shorten or mutate the collection
    Given the collection length or pointer validity can change at an unknown call
    When the optimizer reaches that call
    Then it invalidates affected facts
    And retains bounds checks or emits a valid revalidation guard

Feature: Narrow representations only when unobservable
  Scenario: Internal wide integer stores values 0 through 15
    Given the array is private and its layout does not escape
    When storage representation selection runs
    Then it may pack values into four-bit fields if profitable
    And all reads and writes preserve the values

  Scenario: An address of a public field escapes
    Given external code can observe the field layout
    When storage packing is considered
    Then the source-visible representation is preserved
```

<a id="s6-4"></a>
### 6.4 Cross-language data and runtime cases

```gherkin
Feature: Emulate pointer-based algorithms on pointerless targets
  Scenario: A native byte pointer is converted to a Perl buffer-offset view
    Given the source uses a bounded pointer into a buffer
    When Polypiler.Core emits Perl
    Then it uses a verified buffer and offset representation
    And preserves overlapping writes, byte order and bounds

Feature: Preserve floating formats
  Scenario: MBF64 to C#
    Given a BASIC MBF64 value requiring more than 53 significant bits
    When translated to C#
    Then the result preserves the specified 56-bit significand behavior
    And it does not round-trip through an ordinary double as an equivalent conversion

Feature: Sequence fusion
  Scenario: Pure where-map-reduce over an array
    Given an array with known element type and length
    When a pure filter/map/reduce pipeline is optimized
    Then intermediate collections are absent from optimized IR
    And it produces the same result and exception behavior
```

<a id="s6-5"></a>
### 6.5 Specific showcase scenarios

```gherkin
Feature: Port real MediaDroyd processing kernels
  Scenario: Kotlin CPU deinterlacer to Perl
    Given an isolated Kotlin CPU deinterlacing routine and approved frame fixtures
    When I compile it as a Perl 5 module
    Then the module executes under Perl
    And output frames match the reference within the kernel's declared exactness/tolerance
    And boundary, odd-width, stride and missing-line cases pass

  Scenario: Native NCNN motion interpolation to Rust adapter
    Given an approved NCNN native model provider and separately authorized model assets
    When I generate a Rust adapter
    Then Rust can invoke the provider through a documented stable ABI
    And model load, shape validation, interpolation and resource disposal are tested
    And the manifest clearly states a native NCNN dependency
    And the output never claims pure Rust inference

  Scenario: A truly pure Rust inference request
    Given a model using operators that have no verified Rust implementation
    When I request --backend pure-rust --no-native-deps
    Then the CLI lists missing operators or weight/license blockers
    And emits no falsely verified pure-Rust inference package
```

<a id="s6-6"></a>
### 6.6 BDD evidence per scenario

Each scenario attaches: source hash, target code hash, generated manifest, tool versions, fixture values, stdout/stderr/exit code, reference result, target result, diff, exactness/tolerance policy, opt diagnostics, and CI link. Negative scenarios must cover counterexamples—not just positive happy paths.

---

<a id="s7"></a>
## 7. TDD — testing strategy and release gates

<a id="s7-1"></a>
### 7.1 Test pyramid (tests written with or before production change)

1. **Semantic unit tests** (NUnit, non-fluent `Assert.That`): IR values, types, conversion/overflow, float corner cases, aliases, cost predicates, parser normalization.
2. **Pass unit tests:** before/after IR snapshots, verifier, transformation preconditions, negative tests for overflow, aliasing, NaN, side effects, exception order.
3. **Property-based tests:** random fixed seeds, shrinking, bit-pattern generators across signed/unsigned widths, variable shapes, buffers, strides, holes, `undef`, bigint, NaNs and subnormals.
4. **Metamorphic tests:** equivalent source-language variants normalize to compatible IR; encode-decode roundtrips where lossless; `Translate(A→B→A)` behavior on supported domains.
5. **Differential tests:** compare against the real source interpreter/compiler and independent reference vectors, not a second helper using the same buggy implementation.
6. **Compiler integration tests:** invoke actual `dotnet`, `cargo`, `cc`/`clang`, `g++`/`clang++`, `node`, `perl`, `python`, `javac`, `kotlinc`, PB-Compiler or equivalent with pinned toolchains, as appropriate.
7. **Whole-repository algorithm corpus tests:** each approved upstream fixture is pinned to a commit and run in at least two languages.
8. **Benchmarks:** differential speed/memory/code-size analysis relative to source baseline, previous compiler version and simple reference lowering; record regressions rather than declaring every optimization a win.
9. **Supply-chain/security tests:** dependency/license metadata, sandbox limits, malicious input handling, FFI resource safety, crypto constant-time classification.

<a id="s7-2"></a>
### 7.2 Coverage and acceptance policy

- ≥80% statement/branch coverage for reusable logic; ≥90% for critical type semantics/IR verification/range and rewrite legality; 100% of **documented opcode/type contract partitions** exercised by conformance fixtures. Coverage percentages do **not** replace behavioral validation.
- Every reported supported target pair must **compile and execute** selected generated code in CI (not merely parse generated syntax).
- Numeric exact algorithms: bit-exact oracle and published vectors. Floating-point: per-operation explicit exact/ULP/relative/absolute tolerance; log tolerance policy in manifest.
- Every optimization must have at least one legality-negative fixture and a disabling flag. Differential comparison of `--opt none` against `--opt speed` on randomized fixtures.
- Random/corpus failures retain seed, minimal repro, source and generated code artifacts.
- Output must preserve public API and observable effects or document explicit compatibility mode.
- All unimplemented feature requests fail cleanly with stable diagnostics; silent fallback to wrong arithmetic is a hard release blocker.
- No compiler/optimizer crashes, unbounded hangs or runaway memory on fuzzed malformed source; implement time/memory/input bounds for untrusted compilation.

<a id="s7-3"></a>
### 7.3 Per-language CI matrix

| Layer | Required jobs |
|---|---|
| Core | .NET 9 build/test, warnings as errors, format/lint, coverage, IR verifier, spec validation |
| Native | Linux/macOS/Windows C/C++ compilation and address/UB sanitizers where available |
| Rust | Stable Rust pinned, `cargo fmt`, `clippy`, `cargo test`, Miri where applicable |
| JS/TS | Node LTS pinned, JS tests, TypeScript checker when supported |
| Python/Perl | Pinned Python/Perl version + module/runtime tests |
| Kotlin/Java | JDK + Kotlin compiler pinned, executable fixture comparison |
| BASIC/PB | PB-Compiler test runner, actual supported emulator/oracle differential tests; mark genuine commercial compiler oracle availability separately |
| Full cross corpus | Source→target compile+run, reproducible manifest, license validation |

Build images pin versions, use isolated temp directories, avoid relying on undocumented host software. CI workflow permissions minimal; secrets only in approved settings; no automatic package publication.

---

<!-- PRD content ends -->

[PRD index](../PRD.md) · [Previous: Core architecture](02-core-architecture.md) · [Next: Roadmap and delivery](04-roadmap-and-delivery.md)
