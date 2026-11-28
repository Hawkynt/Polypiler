# Architecture decision records

A decision record states one decision, the options weighed, the evidence and what follows for the
issues that build on it. The [PRD](../PRD.md) stays normative; a record settles what the PRD leaves
open and is cited by the issues that implement it.

A record is `docs/adr/<nnnn>-<slug>.md`: a four-digit number and a lower-case hyphenated slug. The
line after its title is `Area: <area>`. Numbers are allocated in blocks per area — 0001–0029 is the
foundation block, the catalog block starts at 0030, the languages block spans 1000–2999 — and are never reused; a superseded record keeps
its number and names its successor. The pull request that adds a record adds its row here, which is how a number is claimed.
Probe programs and recorded output a record measured live under `docs/adr/evidence/<nnnn>/`.

| Number | Title | Status | Issue |
| --- | --- | --- | --- |
| [0001](0001-ir-stage-invariants.md) | IR stage invariants | superseded in part by [0009](0009-structured-tree-ir.md) | [#19](https://github.com/Hawkynt/Polypiler/issues/19) |
| [0002](0002-numeric-contracts.md) | Numeric contracts: integer overflow, shift, division and rounding | accepted | [#21](https://github.com/Hawkynt/Polypiler/issues/21) |
| [0003](0003-diagnostics-and-exit-codes.md) | Diagnostics registry, renderings and exit codes | accepted | [#17](https://github.com/Hawkynt/Polypiler/issues/17) |
| [0004](0004-cli-design.md) | Command-line grammar, option naming and output conventions | accepted | [#16](https://github.com/Hawkynt/Polypiler/issues/16) |
| [0005](0005-floating-point-tolerance.md) | Floating-point tolerance policy | accepted | [#18](https://github.com/Hawkynt/Polypiler/issues/18) |
| [0006](0006-performance-budgets.md) | Performance budgets | accepted | [#22](https://github.com/Hawkynt/Polypiler/issues/22) |
| [0007](0007-javascript-native-calls.md) | Native calls in JavaScript through a user-supplied binding | accepted | [#1332](https://github.com/Hawkynt/Polypiler/issues/1332) |
| [0008](0008-rust-trap-policy.md) | Traps in generated Rust: a trap policy of panics or error values | accepted | [#1392](https://github.com/Hawkynt/Polypiler/issues/1392) |
| [0009](0009-structured-tree-ir.md) | The structured typed tree IR is the IR | accepted | — |
| [0030](0030-cipher-source-licence.md) | Cipher source licence and its lowest pin | accepted | [#588](https://github.com/Hawkynt/Polypiler/issues/588) |
| [0300](0300-static-types-first.md) | Static types first, dynamic last: one inference framework, semantics tables per language, `object` plus helpers before `dynamic` | accepted | [#1577](https://github.com/Hawkynt/Polypiler/issues/1577) |
| [1000](1000-grammar-described-frontends.md) | Grammar-described frontends: our own PEG generator, overlays per dialect, hand lexers as token sources | accepted | [#1578](https://github.com/Hawkynt/Polypiler/issues/1578) |
| [2600](2600-classic-visual-basic-oracle.md) | The oracle of classic Visual Basic: LibreOffice Basic in VBA mode | accepted | [#1097](https://github.com/Hawkynt/Polypiler/issues/1097) |

Related: [documentation index](../README.md) · [IR and analysis contracts](../architecture/IR.md) · [compilation pipeline](../architecture/PIPELINE.md).
