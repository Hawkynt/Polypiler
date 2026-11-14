# Reference licence texts

The licence scanner identifies a `LICENSE`/`COPYING` file by the fingerprint of its text (see
`LicenceTexts.Fingerprint`), looked up among the files here. Each file is named by the SPDX identifier
it is the text of; the sub-folder only says where it came from. Every file is a verbatim copy; do not
reformat them.

- `spdx/` — the SPDX License List, `text/<identifier>.txt` of
  [spdx/license-list-data](https://github.com/spdx/license-list-data) at tag `v3.28.0`, commit
  `c4a7237ec8f4654e867546f9f409749300f1bf4c`. Licensed CC0-1.0.
- `gnu.org/` — the texts the Free Software Foundation publishes, which differ from the SPDX list's
  in wording details (straight quotation marks, a URL, and the SPDX `LGPL-3.0` texts carry the GPL
  appended) and are what projects actually ship: `https://www.gnu.org/licenses/lgpl-3.0.txt` as
  `LGPL-3.0-only.txt` and `LGPL-3.0-or-later.txt`, `https://www.gnu.org/licenses/gpl-3.0.txt` as
  `GPL-3.0-only.txt` and `GPL-3.0-or-later.txt`, fetched 2026-11-14. Verbatim copying of these texts
  is permitted by their own terms.

A text shared by the `-only` and `-or-later` variants of a licence identifies the family only; the
declared licence of the fixture or the scanned tree selects the variant.
