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
- tests/fixtures/verify/oracle/adler32.vectors.json: the Adler-32 vectors of the oracle framework, the 300 x ff case among them (source: RFC 1950)
- tests/fixtures/backend/golden/: the outputs of both backends for every prototype fixture and variant, recorded by GoldenOutputRecorder before the target syntax trees so that the trees and printers must reproduce them byte for byte (source: the string-building emitters at 2e6b495, built by the C# compiler and run by node in the prototype acceptance tests)
- tests/fixtures/backend/golden/state/: the outputs of the module-state fixtures in every variant of both backends, recorded with GoldenOutputRecorder; every other golden output stayed byte for byte (source: polypiler backends, their calls checked against .NET and node by ModuleStateTests)
- tests/fixtures/backend/golden/spans/*.golden: the outputs of the views fixtures of #1325 in every variant of both backends, new sources whose output nothing recorded before (source: GoldenOutputRecorder)
