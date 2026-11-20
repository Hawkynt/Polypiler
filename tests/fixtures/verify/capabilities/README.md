# Capability report fixtures

Spec trees for `polypiler-tooling report capabilities`. Each tree holds a `spec/` subset only; the
tests copy it into a temporary directory and create `artifacts/gates/` beside it, where the real
`GateRecorder` records the evidence of `tests/fixtures/verify/gates/evidence/` in the test's set-up.
No gate record is committed.

| Tree | Source profiles | Target profiles |
| --- | --- | --- |
| `empty` | none | none |
| `one-verified` | `csharp/net10` (1 diagnosed construct), `javascript/node-es2022` (none) | `csharp/net10`, `javascript/node-es2022` |
| `mixed` | those of `one-verified` and `csharp/net8` (3 diagnosed constructs) | those of `one-verified` and `csharp/net8` |

The set-up of `one-verified` records `csharp/net10 -> javascript/node-es2022` with one fixture up to
`verified`; the set-up of `mixed` records several pairs at different states.
