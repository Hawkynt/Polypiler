# ADR 0030 — Cipher source licence and its lowest pin

Area: catalog

| | |
| --- | --- |
| Status | accepted |
| Index | [decision records](README.md) |
| Date | 2026-11-14 |
| Issue | [#588](https://github.com/Hawkynt/Polypiler/issues/588) (`CAT-CIPHER-LICENCE`) |
| Requirements | [PRD §2](../prd/01-product-and-targets.md#s2) "record license at repository/file/algorithm/model/weight level" |

The catalogue imports of Cipher —
[#589](https://github.com/Hawkynt/Polypiler/issues/589) (`CAT-IMP-CIPHER-CHECKSUM`),
[#592](https://github.com/Hawkynt/Polypiler/issues/592) (`CAT-IMP-CIPHER-HASH`),
[#590](https://github.com/Hawkynt/Polypiler/issues/590) (`CAT-IMP-CIPHER-CIPHERS`),
[#591](https://github.com/Hawkynt/Polypiler/issues/591) (`CAT-IMP-CIPHER-ENCODING`) — rely on this
record, and [#1059](https://github.com/Hawkynt/Polypiler/issues/1059) (`CAT-CIPHER-SOURCE`) enforces
its last clause.

## Question

Under which licence does the catalogue take the files below `Cipher/` of
[`Hawkynt/Hawkynt.github.io`](https://github.com/Hawkynt/Hawkynt.github.io), and from which commit on?

## Decision

1. **Cipher is LGPL-3.0-or-later**, like the rest of that repository. Its code is rung 1 of
   "Sourcing an implementation" in [AGENTS.md](../../AGENTS.md): it can be vendored or converted.
2. **The GPL-3.0 text that `Cipher/LICENSE` carried was a mistake**, corrected upstream by the
   repository owner, who is also Cipher's author, in commit
   [`b3a8a8e92cbca83475986391c3cac56f0b185d29`](https://github.com/Hawkynt/Hawkynt.github.io/commit/b3a8a8e92cbca83475986391c3cac56f0b185d29)
   of 2026-11-13.
3. **The catalogue pins `Hawkynt/Hawkynt.github.io` at that commit or a later one, never an earlier
   one.** A pin is admissible exactly when `b3a8a8e9…` is among its ancestors
   (`git merge-base --is-ancestor b3a8a8e92cbca83475986391c3cac56f0b185d29 <pin>` succeeds). The
   current pin in [`tooling/corpus/corpus.yaml`](../../tooling/corpus/corpus.yaml) and in the pin
   table of `backlog/README.md` is that commit itself.
4. A file inside `Cipher/` that names a third-party origin with its own licence keeps that licence,
   per file, as everywhere else (PRD §2). That is the ordinary per-file rule of the corpus and the
   licence scan, not something this record decides. At the pinned commit no file below `Cipher/`
   other than `Cipher/LICENSE` mentions a GPL or LGPL.

## Commits that must not be pinned

Every commit that does not have `b3a8a8e9…` as an ancestor. Upstream history passed through three
licence states before it; the boundaries are:

| From | Up to and including | Root `LICENSE` | `Cipher/LICENSE` |
| --- | --- | --- | --- |
| first commit | the parent of `8b4c68ee03e8dbd24a196d08602422e392715575` | none | none (`Cipher/` does not exist yet) |
| `8b4c68ee03e8dbd24a196d08602422e392715575` (2024-06-14, "+ LICENSE") | the parent of `ee4b6d881e8c554b83138c40006f8c5fe441d1bf` | GPL-3.0 | none until `2f7d2c0e961264f61174ae0502cee3cba54c0b43` (2025-10-09, "+ license"), GPL-3.0 from there |
| `ee4b6d881e8c554b83138c40006f8c5fe441d1bf` (2026-06-04, "relicense GPL-3.0 to LGPL-3.0-or-later") | `045397b4c934487bc2eb385125fdbd9589502f7f`, the parent of the correction | LGPL-3.0 | GPL-3.0 — the mistake |
| `b3a8a8e92cbca83475986391c3cac56f0b185d29` (2026-11-13) | — | LGPL-3.0 | LGPL-3.0, byte-identical to the root |

`Cipher/` first appears in `ccb1516a3396fd6cdb08593cb17abf83d20f6b98` (2024-12-27), so every
Cipher commit before the correction carried a GPL-3.0 licence file, first at the root and from
2026-06-04 inside `Cipher/` alone. Pinning any of them would bring back a GPL-3.0 licence file the
LGPL-3.0-or-later of this repository cannot take as rung 1.

## Deciding evidence

Captured from a clone of the upstream repository by
[`evidence/0030/run.sh`](evidence/0030/run.sh); the output is
[`evidence/0030/outputs.txt`](evidence/0030/outputs.txt).

- `git log -- Cipher/LICENSE` names exactly two commits: the GPL-3.0 file added in `2f7d2c0e…` and
  the correction:

  ```
  b3a8a8e92cbca83475986391c3cac56f0b185d29 2026-11-13 # Cipher carried the GPL-3.0 text instead of the repository's LGPL-3.0
  2f7d2c0e961264f61174ae0502cee3cba54c0b43 2025-10-09 + license
  ```

- `git show b3a8a8e92cbca83475986391c3cac56f0b185d29:Cipher/LICENSE` begins with
  "GNU LESSER GENERAL PUBLIC LICENSE, Version 3, 29 June 2007". Its SHA-256 is
  `e3a994d82e644b03a792a930f574002658412f62407f5fee083f2555c5f23118`, the same as the root `LICENSE`
  at that commit and the same as `Cipher/LICENSE` in
  [`tooling/corpus/corpus.lock.json`](../../tooling/corpus/corpus.lock.json). In its parent
  `045397b4…` the file begins with "GNU GENERAL PUBLIC LICENSE, Version 3" (SHA-256
  `589ed823e9a84c56feb95ac58e7cf384626b9cbf4fda2a907bc36e103de1bad2`).
- The repository's README at the pin says "Licensed under LGPL-3.0-or-later — see LICENSE"; the
  licence text alone does not carry the "or later".
- The owner's statement of **2026-11-13**, the message of the correcting commit, authored by Hawkynt:

  > Cipher/LICENSE held the text of the GNU General Public License v3, while the repository root and
  > every other project in it are licensed under the GNU Lesser General Public License v3. The GPL
  > file was a mistake: Cipher is LGPL-3.0 like the rest. The file now carries the same LGPL-3.0 text
  > as the root LICENSE.

## Options

1. **Record the correction (chosen).** Cipher is LGPL-3.0-or-later from `b3a8a8e9…` on; earlier
   commits are not pinned.
2. No alternative was under consideration. Treating Cipher as GPL-3.0 would contradict the author's
   own statement and the corrected file.

## Consequences

- The corpus entry for `Hawkynt/Hawkynt.github.io` keeps `licence: LGPL-3.0-or-later` and lists
  `Cipher/LICENSE` among its `licenceFiles`. The lock therefore records the hash of the corrected
  text, and any change of pin shows up in review as a changed lock entry for `Cipher/LICENSE`.
- Moving the pin forward needs no new record. Moving it backwards past `b3a8a8e9…` is not allowed;
  the pin guard of `CAT-CIPHER-SOURCE` turns that rule into a check.
- No follow-up issue is needed.
