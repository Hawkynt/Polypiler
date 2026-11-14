# Licence policy decisions

Every identifier of the `allowed`, `referenceOnly` and `allowedExceptions` lists of `policy.yaml` has
a row here, added by the repository owner in the commit that changes the list. The last row of an
identifier names the list it is in; `polypiler-tooling licenses scan` refuses a policy that disagrees.
A request not decided yet is recorded under `pendingDecisions` in `policy.yaml`, never here.

| Identifier | List | Date | Reason | Asked by |
| --- | --- | --- | --- | --- |
| `LGPL-3.0-or-later`, `LGPL-3.0-only`, `LGPL-2.1-or-later` | allowed | 2026-11-13 | initial policy, PRD §13.3 | INFRA-LICENSES |
| `MIT`, `BSD-2-Clause`, `BSD-3-Clause`, `Apache-2.0`, `ISC`, `Zlib`, `0BSD`, `Unlicense`, `CC0-1.0` | allowed | 2026-11-13 | initial policy, PRD §13.3 | INFRA-LICENSES |
| `LGPL-2.0-or-later` | allowed | 2026-11-13 | "I allow LGPL code, as well as GPL, MIT, Apache and others." | CAT-IMP-MEDIADROYD |
| `LGPL-2.0-only` | allowed | 2026-11-13 | same sentence | CAT-IMP-MEDIADROYD |
| `GPL-2.0-or-later` | allowed | 2026-11-13 | same sentence | INFRA-LICENSES |
| `GPL-3.0-only` | allowed | 2026-11-13 | same sentence | INFRA-LICENSES |
| `GPL-3.0-or-later` | allowed | 2026-11-13 | same sentence | INFRA-LICENSES |
| `GPL-2.0-only`, `AGPL-3.0-only`, `AGPL-3.0-or-later` | referenceOnly | 2026-11-13 | not covered by the sentence; kept as oracles only | INFRA-LICENSES |
