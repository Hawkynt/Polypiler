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
