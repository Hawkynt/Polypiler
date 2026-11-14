# Golden file changes

Every deliberate change to a golden file - a `*.vectors.json`, a file in a `golden/` folder, a
`golden.provenance.json` or a file one covers - adds one line here in the same pull request, in a
commit that changes nothing outside `tests/fixtures/`, `tests/vectors/`, `tests/features/` and
`backlog/`, after `polypiler-tooling integrity seal <path>...` recorded it in `golden.lock.json`.
`polypiler-tooling integrity check --base <ref>` holds every pull request to this. The source is the
file's provenance source.

- tests/fixtures/verify/adler32.vectors.json: the first vectors, Adler-32 of "", "a" and "abc" from zlib (source: python)
- tests/vectors/float/golden.provenance.json: the MPFR reference vectors become golden files (source: mpfr)
- tests/fixtures/prototype/js/recorded/golden.provenance.json: the outcomes recorded from node become golden files (source: node)
