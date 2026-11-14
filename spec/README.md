# Spec documents

Everything Polypiler treats as normative fact — IR operations, types, effects, language subsets,
rewrites — is written down here as a checked YAML document, one per file, under
`spec/<family>/`. `polypiler-tooling spec validate` reads every one of them and refuses a document
that breaks a rule below. A capability may only be claimed against a document it accepts.

```
spec/
  diagnostics.yaml        the diagnostics registry (ADR 0003); not a spec document
  schemas/                JSON Schema 2020-12: meta.schema.json, and <family>.schema.json per family
  <family>/**/*.yaml      the documents of one family
```

## The metadata block

Every document starts with these keys, in this order, then the family body under `spec:`. No other
top-level key is allowed. [`schemas/meta.schema.json`](schemas/meta.schema.json) states the types;
the validator adds the rules a schema cannot state.

| Key | Type | Rule |
| --- | --- | --- |
| `id` | string | `<FAMILY>.<name>`: `FAMILY` is the family folder in upper case (`demo` → `DEMO.`); `name` is dot-separated parts of letters, digits, `_` and `-`, each starting with a letter or digit; unique across `spec/` |
| `title` | string | 1–120 characters |
| `version` | integer | ≥ 1; raised by every normative change |
| `status` | enum | `draft`, `normative`, `deprecated` |
| `owner` | string | a GitHub login or team |
| `issue` | string | a backlog issue ID (`SPEC-META`) or `#<number>` (quote it: `"#55"`, or YAML reads a comment) |
| `tests` | list of string | repository-relative paths that exist inside the repository; at least one when `status` is `normative` |
| `introducedIn` | enum | `P0` … `P6` |
| `deprecatedIn` | enum, optional | `P0` … `P6`; present exactly when `status` is `deprecated`; not earlier than `introducedIn` |
| `compatibility` | enum | `none`, `additive`, `breaking`: the impact of this version on the previous one; `none` at version 1 |
| `reviewedRevision` | integer | 0 … `version`; a `normative` document is reviewed at its current version |
| `normative` | string | the normative text, RFC 2119 keywords in capitals; may be empty only while `status` is `draft` |

## The YAML

The file is read with the YAML 1.2 core schema: `no`, `on` and `yes` are strings, and so is anything
quoted — `version: "2"` is a string and fails, it is never coerced to 2. Anchors, aliases, merge keys
(`<<`), explicit tags, a key given twice and more than one document per file are refused, so a document
means the same in every YAML library.

## Adding a document

1. Pick the family folder and a file name; give the document the ID `<FAMILY>.<name>`.
2. Write the metadata block with `status: draft`, `version: 1`, `compatibility: none`,
   `reviewedRevision: 0`, then the body under `spec:` as the family's schema describes it.
3. Run `polypiler-tooling spec validate --family <folder>` until it prints `… 0 errors`.
4. To make it normative: write the `normative` text, list at least one test that holds it, have it
   reviewed and set `reviewedRevision` to `version`.

Every later change to the normative text or the body raises `version`, sets `compatibility` to what the
change does to readers of the previous version, and leaves `reviewedRevision` behind until the review.

## Adding a family

A family is one class implementing `ISpecFamily` (`Polypiler.Tooling/Spec/`): its `Folder`, the
`SchemaPath` of its body schema under `spec/schemas/`, and `Check(document, tree)` for the rules the
schema cannot state. Add it to `SpecFamilies.All`. The engine does everything else — finding the files,
reading the YAML with positions, the metadata block, the ID index — and calls `Check` only for a
document whose metadata and body are valid, so `document.Meta` is set and the body has the schema's
shape. Inside `Check`:

- report a broken rule with `SpecDiagnostics.Rule(document, "/spec/…", reason, rule)`, which is
  `PLY0046: <reason> (rule: <rule>)` at that value;
- resolve every body value that names another document with `tree.Resolve(document, "/spec/…", id)`,
  which is `PLY0043: unknown spec id '<id>'` when nothing has that ID.

`Polypiler.Tooling/Spec/Families/DemoSpecFamily.cs` with
[`schemas/demo.schema.json`](schemas/demo.schema.json) is the worked example; its documents live in the
test fixtures (`tests/fixtures/spec/meta/`), not here.

## Validating

```
polypiler-tooling spec validate [--root <dir>] [--family <folder>] [--diagnostics text|json]
```

The root is `spec` unless given. With `--family` only that family's documents are validated and
counted, but the whole tree is read, so references into other families resolve. Test paths and family
schemas are relative to the repository: the nearest directory above the root that holds `.git`, else
the current directory. Each document goes through YAML → family known → metadata schema → metadata
rules → family schema → family rules and stops at the first step that fails, so one defect gives one
diagnostic. Diagnostics go to standard error sorted by path, line, column and code; the last line of
standard output is `<n> documents, <m> errors`; the exit code is 0 without errors and 1 with.

| Code | Meaning |
| --- | --- |
| `PLY0040` | `<json pointer>: <schema message>`: the metadata block or the body breaks its schema |
| `PLY0041` | a metadata rule: family prefix of `id`, tests of a normative document, review at the current version, `deprecatedIn` with `status`, `compatibility` at version 1 |
| `PLY0042` | `duplicate spec id '<id>', first defined in <path>`, on the later path |
| `PLY0043` | a test path that does not exist or lies outside the repository; an unknown spec ID |
| `PLY0044` | `deprecatedIn` earlier than `introducedIn`; `reviewedRevision` above `version` |
| `PLY0046` | a family rule, `<reason> (rule: <rule>)` |
| `PLY0047` | a file that is not valid YAML, an unknown family, an unreadable schema, a missing root |

## Family `binary`

CPU instruction tables, token tables of BASIC dialects and binary file layouts:
`binary/<name>/instructions.yaml`, `binary/<name>/tokens.yaml` and `binary/<name>/format.yaml`, with the
IDs `BINARY.<name>.instructions`, `.tokens` and `.format`. [`schemas/binary.schema.json`](schemas/binary.schema.json)
states the body of each kind. Opcodes, token values and magic bytes are upper-case hex strings, quoted
(`"0xA9"`), or YAML reads a number. Opcodes, lengths, cycles and token values are copied from the
vendor's manual, never filled in from memory; `documented: false` marks an opcode a lifter refuses.
The rules (`PLY0046`): `layout`, `register-unique`, `opcode-unique`, `mode-declared`, `flags-declared`,
`length-matches-mode` (opcode bytes plus the mode's `operandBytes`), `token-unique`, `token-in-range`
and `fields-ordered`. `cpu.parent` names the instruction document a variant extends (`PLY0043`).

## Family `effects`

`spec/effects/<name>.yaml`, ID `EFFECTS.<name>`, body [`schemas/effects.schema.json`](schemas/effects.schema.json):
one effect class and whether an operation with it may be reordered (`ordering`), duplicated
(`duplication`) or eliminated (`elimination`), the region kinds it `reads` and `writes`, `mayTrap`,
`defaultForUnknown`, and `targets.unsupported`, the diagnostic a target that cannot honour it raises
instead of dropping the operation. [`effects/unknown-call.yaml`](effects/unknown-call.yaml) is the
default: the effect of every operation nothing else describes. Rules (`PLY0046`):

| Rule | What it requires |
| --- | --- |
| `layout` | the file lies directly in `effects/`; its ID is `EFFECTS.<file name>` and `name` is the file name |
| `single-default` | exactly one document has `defaultForUnknown: true`; when several do, the one of class `unknown-call` is the default and the others are reported |
| `default-is-conservative` | the default is `strict`, duplication and elimination `forbidden`, and reads and writes `unknown` |
| `volatile-is-strict` | classes `volatile`, `mmio`, `device-io`, `code-write` and `unknown-call` are `strict`, duplication and elimination `forbidden` |
| `write-implies-order` | an effect that writes anything is not `free` |
| `unique-name` | no two documents share a `name` |

## Family `intrinsics`

`spec/intrinsics/<group in lower case>/<name in lower case>.yaml`, ID `INTRINSICS.<name>`: the contract
of one intrinsic (PRD §4.1, §4.3), which a backend lowers from and a recognizer rewrites to.
[`schemas/intrinsics.schema.json`](schemas/intrinsics.schema.json) states the body: `name` (PascalCase),
`group` (`IntMath`, `FloatMath`, `Boolean`, `Bitfield`, `BitCount`, `Shuffle`, `Saturation`, `Carry`,
`Endian`, `Trig`, `Sequence`, `Matrix`, `Tensor`, `CopyFill`), `typeParameters` `{name, constraint}`
(`integer`, `unsigned`, `signed`, `float`, `any`), `parameters` (0–8) and `returns` (1–4) `{name, type}`,
`legality` `{id, text, fact}`, `effects` (`[pure]` or `EFFECTS.` IDs), `exactness` (`{mode: exact}`, or
`{mode: tolerance, policy: VERIFICATION.tolerance.<name>}`), `reference` `{algorithm, source}`,
`mustSurviveUntil` (the last `IR.stage.` that still holds it unlowered) and at least three `vectors`
`{types, inputs, outputs}`; optional are `attributes`, `algebra`, `lowersTo`, `facts`, `laneLiftable`,
`requiresRuntime`, `spelling` and `accuracy` (`ulp(<n>)`, `cr` or `library(<name>)`, tolerance only).

A `type` is a type parameter or a `TYPES.` ID; a trailing `[]` makes it an array of that type, quoted in
YAML (`"TYPES.u8[]"`). A vector literal is a quoted hexadecimal bit pattern (`"0x3FF0000000000000"` for
the `f64` 1.0), which must lie below 2^bits, or a decimal integer, which must lie in the range of the type
— two's complement for a signed type; an array value is a list of such literals. A NaN output stands for
any NaN. The rules (`PLY0046`):

| Rule | What it requires |
| --- | --- |
| `layout` | the path follows from `group` and `name`, the ID is `INTRINSICS.<name>`; when it fails, nothing else is checked |
| `type-bound` | every type is declared; every vector binds each type parameter, and only those, to a type its constraint admits |
| `vector-arity` | one input per parameter and one output per return value |
| `vector-width` | each literal fits the type bound to it; a list exactly for an array type |
| `unique-name` | no two documents share a name, ignoring case, or a `spelling` |
| `integer-is-exact` | groups other than `FloatMath`, `Trig`, `Matrix` and `Tensor` are `exact` |

Every `TYPES.`, `EFFECTS.`, `IR.`, `INTRINSICS.` and `VERIFICATION.` ID resolves or fails with `PLY0043`.
The IDs are stable: a recognition rule names an intrinsic by them. The seed is every exact intrinsic the
prototype IR implements (`IrIntrinsicOp`), 85 documents with vectors worked out from their references,
not from Polypiler: the integer and bit operations, saturation, carry and overflow, the endian loads and
stores, and the floating-point operations whose contract is exact. The array operations and the
floating-point operations with a `ulp(n)` contract, the integer `Log2`'s floating-point half among them,
follow once their tolerance documents exist.

## Family `ir`

The IR operations, stages and version (PRD §4.1–§4.3, [ADR 0001](../docs/adr/0001-ir-stage-invariants.md)),
checked by [`schemas/ir.schema.json`](schemas/ir.schema.json) and `IrSpecFamily`. The body's `kind`
selects the shape, and the path follows from it:

| `kind` | Path and ID | Body |
| --- | --- | --- |
| `operation` | `ir/operations/<mnemonic in lower case>.yaml`, `IR.<mnemonic in lower case>` | `mnemonic` (PascalCase), `category`, `typeParameters` `{name, constraint}`, `operands` `{name, type}` (0–8), `result` `{type}` or `null`, `flags`, `effects` (`[pure]` or `EFFECTS.` IDs), `traps` `{when, kind}`, `stages` `{from, to}`, `examples` (≥ 1, each `{flag?, operands, result}` or `{flag?, operands, trap}`; a literal is `"0x…"` or a decimal integer) |
| `stage` | `ir/stages/<name>.yaml`, `IR.stage.<name>` | `order` (1–999, the order of the ADR's stage list), `invariants` `{id, text}` (≥ 1) |
| `version` | `ir/version.yaml`, `IR.version` | `irVersion`, `serializationVersion` (≥ 1) |

An operand or result `type` is a type parameter of the operation or a `TYPES.` ID. Quote the trap kind
`"null"`: plain `null` is no value in YAML. The rules, each `PLY0046 … (rule: <name>)`: `layout`,
`stage-range` (`stages.from` not after `stages.to`, both stages), `example-arity`, `flag-declared` (an
operation with flags needs one in every example, one without flags in none), `trap-declared`,
`type-declared`, `unique-mnemonic`, `unique-stage-order`. Stage, effect and type IDs resolve or fail
with `PLY0043`.

The documents describe the typed SSA IR the `IR-` issues build. Where the prototype's structured IR
(`Polypiler.Core.IR`) already implements an operation, the `normative` text says how far:
`ir/operations/shl.yaml`, for instance, notes that `IrBinaryOp.ShiftLeft` is the `mask` variant with an
`i32` amount.

## Family `languages`

`spec/languages/<identifier>/language.yaml` (ID `LANGUAGES.<identifier>`) declares a source language:
the `--from` identifier, which is also the folder name, its display name, the language code of
`backlog/README.md` and the first code of its diagnostic block. Each
`spec/languages/<identifier>/profiles/<profile>.yaml` (ID `LANGUAGES.<identifier>.<profile>`) is one
version of it: the toolchain key of `tooling/toolchains.lock`, evaluation order, integer overflow,
division and remainder, shift counts, string encoding, exceptions, memory model, the runtime that
executes it, and the construct table of the frontend — each construct `supported` or `diagnosed` with
a code of the language's frontend range (block to block + 49). A profile records what the language
defines; it claims no capability. [`schemas/languages.schema.json`](schemas/languages.schema.json) is
the body; the rules (`PLY0046`) are `layout`, `unique-identifier` (backlog code, diagnostic block),
`profile-has-language`, `toolchain-known`, `unique-construct`, `diagnosed-needs-code` and
`diagnostic-in-block`; a profile's `language` that names no document is `PLY0043`.

The seed holds C# with one profile per target framework the C# backend writes (`net2.0` … `net10.0`)
and JavaScript with one per level the JavaScript backend writes (`es5` … `es2022`). Only the profiles
the frontends read today, `csharp/net10.0` and `javascript/es2022`, carry constructs.

## Family `permutations`

Bit mappings, the grammars that synthesize them and the cost tables of a CPU (PRD §4.1, §23), body
schema [`schemas/permutations.schema.json`](schemas/permutations.schema.json). A document of kind
`mapping`, `grammar` or `cost` lives in `permutations/<kind>s/<name>.yaml` with the ID
`PERMUTATIONS.<kind>.<name>`. A mapping is exactly its `outputs` list, least significant output bit
first: an input bit index, `{not: <index>}`, `{const: 0|1}` or `{dontCare: true}`; when an output is a
don't-care, every vector needs a `mask` of the compared bits. The rules (`PLY0046`):

| Rule | What it checks |
| --- | --- |
| `layout` | the folder matches `kind`, the ID matches the path |
| `index-in-range` | every source index, plain or under `not`, is below `inputWidth` |
| `bijective-consistent` | `bijective: true` exactly when there are `inputWidth` plain outputs, each input bit once |
| `vector-width` | `input` fits `inputWidth`; `output` and `mask` fit the number of outputs |
| `vector-consistent` | every vector's `output` is the mapping applied to `input`, on the masked bits that are not don't-care |
| `unique-primitive` | a cost table prices each primitive once |
| `cost-evidence` | a `measured` cost names an existing file under `docs/benchmarks/`; a `documented` one a non-empty citation |

A grammar's `target` names an existing `TARGETS.` document (`PLY0043`). The seed document is
`permutations/mappings/reverse8.yaml`.

## Family `recovery`

`spec/recovery/O<4 digits>.yaml`, ID `RECOVERY.O<4 digits>`: one recovery rule of the block
`O0170`–`O0199` (de-unrolling, expression slicing, compiler idioms, loop and structure reconstruction).
[`schemas/recovery.schema.json`](schemas/recovery.schema.json) states the body. Every rule is `proven`,
with at least one proof obligation, or `speculative-guarded`, with a `speculation` guard and fallback;
there is no mode for an unguarded guess. `RecoverySpecFamily` adds the rules `layout`, `id-in-block`
(a number of the bit-permutation or object-model recovery block is told which family it belongs to),
`speculation-matches-mode`, `proven-needs-obligation`, `deunroll-needs-repetitions`,
`effects-preserved` (a rule whose forms hold a `Store` or a `Call` preserves `memory-effects` and
`effect-order`) and `switch-matches`. Its fixtures are in `tests/fixtures/spec/recovery/`.

## Family `rewrites`

`spec/rewrites/O####.yaml`, ID `REWRITES.O####`: one semantics-preserving transformation of the blocks
`O0001`–`O0099` and `O0230`–`O0299` — its `before` and `after` IR text, `predicates`, `proof` class,
`floatingPoint` claims, `negativeFixtures`, `switch`, `optLevels`, `interactions` and optimization page
([`schemas/rewrites.schema.json`](schemas/rewrites.schema.json)). Recognition rules (`O0100`–`O0169`)
are not rewrites of this family. Its rules, each `PLY0046`:

| Rule | What it refuses |
| --- | --- |
| `layout` | a path other than `rewrites/O####.yaml`, or an ID, `optimization` or `document` that does not follow from the file name; stops the other rules |
| `id-in-block` | a number outside `O0001`–`O0099` and `O0230`–`O0299` |
| `no-unproven-reassociation` | `reassociates` or `contracts` without proof class `float-exactness` and a predicate with fact `float-exactness`; there is no waiver |
| `float-flags-consistent` | `reassociates` or `contracts` while `touchesFloat` is false |
| `interaction-acyclic` | a cycle of `interactions.after` over all documents, reported once at its smallest optimization |
| `switch-matches` | a `switch` that disables another optimization |
| `negative-fixture-exists` | a normative document whose negative fixture is not a file in the repository |

Every optimization under `interactions` must have a document (`PLY0043`). The valid example and one
fixture per rule and per required property live in `tests/fixtures/spec/rewrites/`.

## Family `types`

`spec/types/<family>/<name>.yaml`, ID `TYPES.<name>`: one descriptor per type, with semantic width
(`bits`), storage (`layout`) and signedness kept apart (PRD §4.4).
[`schemas/types.schema.json`](schemas/types.schema.json) states the body; `TypesSpecFamily` adds the
rules `layout` (the folder is `spec.family`, the ID follows from the file name), `bits-fit-layout`
(`layout.sizeBytes × 8 ≥ bits`), `example-width` (every `values[].bits` fits in `bits`),
`distinct-symmetric` (a type in `distinctFrom` lists this one back), `alignment-power-of-two` and
`family-requirements` (`overflow` for integer and fixed; `rounding` for float, fixed, decimal, posit
and mbf; `nan` for float, posit and mbf; `subnormals` for float and mbf; `lanes` exactly for vector;
`encoding` exactly for encoded-integer; signedness `none` only for boolean, text and custom). The seed
documents are the scalar types of the IR: `bool`, `i8` … `u64`, `f32` and `f64`.

## Family `targets`

`spec/targets/<language identifier>/<profile>/target.yaml`, ID `TARGETS.<language identifier>.<profile>`:
one target profile, the one `--target-profile` selects, with its `abi` (`name`, `byteOrder`,
`pointerBits`), `pointers`, `unions`, `physicalTypes` (each `TYPES.` ID with its native spelling and
whether generated code needs a helper for it), `alignment`, `simd` features, `limits`, runtime
`dependencies` (SPDX licence) and `packaging` (PRD §4.1, §4.7). The document states facts about the
target; choosing native or helper for a program stays with the legalizer.
[`schemas/targets.schema.json`](schemas/targets.schema.json) is the body. Rules (`PLY0046`):

| Rule | What it requires |
| --- | --- |
| `layout` | the path has the form above and the ID follows from it (nothing else is checked otherwise); `language` is `LANGUAGES.<first folder>` and `profile` the second folder |
| `pointer-consistency` | `pointers: native` has `abi.pointerBits` above 0; `none` and `fat` have 0 |
| `limit-order` | `limits.exactIntegerBits` does not exceed `limits.maxIntegerBits` |
| `toolchain-known` | `toolchain` is a key of `tooling/toolchains.lock` |
| `type-fits-target` | an `integer` type mapped with `helper: false` is not wider than `limits.maxIntegerBits` |
| `unique-type` | each type is listed once in `physicalTypes` |
| `unique-target` | no earlier document in path order has the same `language` and `profile` |

`language` and every physical `type` resolve or fail with `PLY0043`. `maxIntegerBits` is the widest
fixed-width integer the target computes with natively, `exactIntegerBits` the widest integer its
default number type holds exactly. The seed is one target per profile the backends write, with the
toolchain of the language profile of the same name: C# `net2.0` … `net10.0` (verifiable code, so no
pointers; explicit struct layout gives native unions; every scalar type native) and JavaScript `es5` …
`es2022` (from ECMA-262: a Number holds integers exactly up to 2^53; `BigInt` makes 64-bit integers
native from ES2020, below that they are a `{ hi, lo }` pair behind a helper; `Math.fround` makes `f32`
native from ES2015). The fixtures are in `tests/fixtures/spec/targets/`.

## Family `verification`

What a comparison of generated code with its oracle runs under (PRD §4.1, §7.2, §23), body schema
[`schemas/verification.schema.json`](schemas/verification.schema.json). A document of kind `generator`,
`tolerance` or `oracle` lives in `verification/<kind>s/<name>.yaml` with the ID
`VERIFICATION.<kind>.<name>`.

| Kind | Body |
| --- | --- |
| `generator` | `type` (a `TYPES.` ID) and `strategy`: `boundary` with the explicit `values` (quoted bit patterns `"0x…"` or decimal integers, unique), `uniform` and `bit-pattern` with `seed` (0 … 4294967295) and `count` (1 … 1000000), `corpus` with neither |
| `tolerance` | `operation` (an `INTRINSICS.` or `IR.` ID), `mode` (`exact`, `ulp`, `relative`, `absolute`), `bound` (> 0, a whole number in `ulp` mode), `specialValues` (`bitwise`, `signed-zero` when absent, `class`): the modes and rules of [ADR 0005](../docs/adr/0005-floating-point-tolerance.md). The ID is the policy name vector files and manifests refer to |
| `oracle` | `implementation` (what produces the reference result: `node`, `published-vectors`, …), `toolchain` (a key of `tooling/toolchains.lock`, optional), `observes` (≥ 1 of `return-value`, `buffers`, `flags`, `registers`, `memory-writes`, `io`, `external-calls`, `traps`, `cycles`) |

An operation without a tolerance document is compared `exact`; a tolerance document's bound is the one
ADR 0005's formula gives, never a number that makes a fixture pass. Changing a seed, a value or a bound
raises `version`. The rules, each `PLY0046`:

| Rule | What it refuses |
| --- | --- |
| `layout` | a folder other than the kind's, or an ID that does not follow from the path |
| `strategy-requirements` | a generator without a property its strategy requires, or with one it does not take |
| `value-fits-type` | a value wider than the type's `bits`; a decimal outside an integer type's range |
| `boundary-covers-extremes` | a `boundary` generator of an integer type without the type's minimum and maximum |
| `bound-for-mode` | `exact` with a `bound`, any other mode without one |
| `one-tolerance-per-operation` | a second tolerance document for an operation, reported on the later path |
| `oracle-not-self` | an oracle whose `implementation` is `polypiler` |

A generator's `type` and a tolerance's `operation` resolve or fail with `PLY0043`. The seed document is
`verification/generators/u32-boundary.yaml`; tolerance documents arrive with the intrinsics they belong
to. The fixtures live in `tests/fixtures/spec/verification/`.
