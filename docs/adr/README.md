# Architecture decision records

A decision record states one decision, the options weighed, the evidence and what follows for the
issues that build on it. The [PRD](../PRD.md) stays normative; a record settles what the PRD leaves
open and is cited by the issues that implement it.

A record is `docs/adr/<nnnn>-<slug>.md`: a four-digit number and a lower-case hyphenated slug. The
line after its title is `Area: <area>`. Numbers are allocated in blocks per area — 0001–0029 is the
foundation block — and are never reused; a superseded record keeps its number and names its
successor. The pull request that adds a record adds its row here, which is how a number is claimed.
Probe programs and recorded output a record measured live under `docs/adr/evidence/<nnnn>/`.

| Number | Title | Status | Issue |
| --- | --- | --- | --- |
| [0001](0001-ir-stage-invariants.md) | IR stage invariants | accepted | [#19](https://github.com/Hawkynt/Polypiler/issues/19) |
| [0002](0002-numeric-contracts.md) | Numeric contracts: integer overflow, shift, division and rounding | accepted | [#21](https://github.com/Hawkynt/Polypiler/issues/21) |

Related: [documentation index](../README.md) · [IR and analysis contracts](../architecture/IR.md) · [compilation pipeline](../architecture/PIPELINE.md).
