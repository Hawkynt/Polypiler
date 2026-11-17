# Target profiles

One folder per target profile, `spec/targets/<language identifier>/<profile>/`. `target.yaml` is the spec
document of the family `targets` ([spec/README.md](../README.md#family-targets)); `cost.yaml` beside it is the
profile's cost profile, which the spec validator skips and the cost model checks.

## `cost.yaml`

The abstract costs the cost model (`Polypiler.Core.Optimize.Cost`, [TARGETS.md](../../docs/architecture/TARGETS.md#cost-model))
ranks equivalent forms by, PRD §4.8 and §23.5. Every number is a non-negative integer in units the profile
chooses; only their ratios matter. A form the file does not list is unavailable on the target. Schema:
[`schemas/cost-profile.schema.json`](../schemas/cost-profile.schema.json).

| Key | Required | Content |
| --- | --- | --- |
| `profile` | yes | the profile's name |
| `unknownTripCount` | yes | the count used for an element count, trip count or amount of work that is neither a constant nor in a proven range (a range gives its midpoint) |
| `default` | yes | a cost: what an operation the profile does not list costs; a decision priced by it says `default cost` |
| `operations` | no | map `Op.type` → forms; each form one of `native`, `sequence` (a cost each) and `helper` (`call`: the cost of one call, the body's execution included; `body`: the cost of the body, counted once per module), at least one per operation |
| `loop` | yes | `overhead`: the test and step of one loop iteration |
| `swar` | no | `registerBits` (≥ 1), `setup`, `perBlock`: SIMD within an integer register |
| `simd` | no | list of `width` (≥ 1), `setup`, `perBlock`, one per vector width |
| `threads` | no | `count` (≥ 1), `spawn`: worker threads and the cost of starting and joining them |
| `gpu`, `npu` | no | `transfer`, `perElement`: an accelerator and the cost of moving the data there and back |

A cost is a mapping of `latency` and `size` (required) and `memory` and `allocations` (optional, 0 when absent).
A missing `swar`, `simd`, `threads`, `gpu` or `npu` means the target has none.

`Op.type` names an operation of the IR at a scalar type: `Op` is a member name of `IrBinaryOp`, `IrUnaryOp` or
`IrIntrinsicOp` (`Xor`, `Negate`, `RotateLeft`, `PopCount`), `type` the lower-case name of a scalar `IrTypeKind`
(`bool`, `i8`, `u8`, `i16`, `u16`, `i32`, `u32`, `i64`, `u64`, `f32`, `f64`). The element width of the type sets
the lanes of a SWAR register or a vector (`bool` counts as 8 bits); a register narrower than one element offers
no such path.

```yaml
profile: narrow
unknownTripCount: 16
default: { latency: 2, size: 2 }
operations:
  RotateLeft.u32:
    sequence: { latency: 3, size: 3 }
    helper:
      call: { latency: 12, size: 2 }
      body: { latency: 0, size: 20 }
  Xor.u8:
    native: { latency: 1, size: 1 }
loop: { overhead: 2 }
swar: { registerBits: 64, setup: 4, perBlock: 1 }
```

The formulas the model prices with (`CostFormulas`):

| Form | Latency | Size |
| --- | --- | --- |
| native, sequence at `s` call sites | `s × latency` | `s × size` |
| helper at `s` call sites | `s × call + body` | `s × call + body` |
| loop of trip `t`, body `b` | `t × (b + overhead)` | `b + overhead` |
| unrolled | `t × b` | `t × b` |
| scalar over `n` elements | `n × op` | `op` |
| SWAR / SIMD over `n` elements, `lanes = bits / element bits` | `setup + (n / lanes) × perBlock + (n mod lanes) × op` | `setup + perBlock + op` |
| threads over `w` units of work | `spawn + (w + count − 1) / count` | 0 |
| gpu, npu | `transfer + w × perElement` | 0 |
| cpu | `w` | 0 |

Diagnostics: `PLY0355` (exit 3) — `target profile '<language>/<profile>' has no cost profile`; the model never
falls back to a default profile. `PLY0356` (exit 4) — `cost profile '<path>': <key>: <problem>`, at the line of the
node: invalid YAML, a missing required key, a negative or non-integer number, an unknown key, or an operation the
IR does not know.

No target profile ships a `cost.yaml` yet; the backend issue of each target adds its own with numbers it can
justify. The test profiles are `tests/fixtures/optimize/cost/wide.yaml` and `narrow.yaml`.
