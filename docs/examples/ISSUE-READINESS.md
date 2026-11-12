# Issue-readiness examples

Hypothetical work orders, not live issues, implementations or test results. The
[issue-writing standard](../../ISSUES.md) is authoritative; these examples explain its application.

## Ready example

**Title:** `spec: document the existing architecture navigation`

**Review:** Hypothetical maintainer review, 2026-11-12; decision `state:ready`.
No implementation or passing tests are required to be Ready. All inputs for this documentation-only
slice are available; the unchecked implementation steps below are expressed as a numbered plan,
not as completed work. No actual readiness approval or implementation is asserted by this example.

### Why

PRD §8 P0 requires contributor guidance. The existing architecture pages have a navigation index;
explain that reading order consistently from the repository README so a contributor can find the
semantic and verification contracts before changing code.

### User stories

- As a **Compiler contributor**, I want the architecture reading order linked from the README,
  so that I can find the existing semantic and verification contracts before editing the compiler.

### Scope

**In scope**
- Link the existing documentation index from the README's Documentation section and explain that
  the pages describe intended architecture.
- Check relative links and retain the repository README section order.

**Out of scope**
- Compiler features, new architecture pages, ADRs, tooling and policy changes: not planned here.

### Preconditions and inputs

- **Depends on:** none; the index and pages already exist at the pin below. Zero unmet prerequisites.
- **Fixtures:** `Hawkynt/Polypiler@134668b4d02e8fa859b947e9b1d5015ce4cce8eb:README.md`
  and `Hawkynt/Polypiler@134668b4d02e8fa859b947e9b1d5015ce4cce8eb:docs/README.md`,
  LGPL-3.0-or-later. External source corpus: N/A — no translation or imported code.
- **Oracle:** the tracked documentation index and repository README convention. Foreign runtime
  oracle: N/A — no executable product behavior changes.
- **Toolchains:** .NET SDK 10.0.112 for existing build/tests; existing CI selects `10.0.x`.
- **Sourcing:** rung 3, original in-repository documentation. Rungs 1–2 do not apply because no
  third-party implementation is imported.
- **Owner:** author supplies the link evidence; maintainer or named delegate reviews it.

### Specification

Update only `README.md` and its documentation navigation tests. Keep the existing section order,
architecture links and distinction between planned and shipped features. All relative destinations
must exist in the tracked tree. No public API, IR, ABI or CLI changes. Product diagnostic codes:
N/A — a missing link is a documentation-check failure identifying its source and target.

### Acceptance criteria (Gherkin)

```gherkin
Feature: Find the existing architecture documentation
  Scenario: A contributor follows the documentation link
    Given README.md links to docs/README.md
    When the navigation check resolves that relative link
    Then docs/README.md exists in the tracked tree
    And the README describes intended architecture without claiming shipped support

  Scenario: A missing target is caught
    Given a test fixture links to missing-architecture-index.md
    When the navigation check resolves that relative link
    Then the check fails with the source path and missing target
```

Copy these scenarios into `tests/features/infrastructure/architecture-navigation.feature` and bind
them in the NUnit documentation fixture. They are proposed tests, not results.

### Test design

| # | Technique | Class / boundary | Input | Expected |
| --- | --- | --- | --- | --- |
| 1 | Happy path | Existing target | docs/README.md | Resolves to tracked index |
| 2 | Exception | Missing target | missing-architecture-index.md | Failure names source and target |
| 3 | Boundary | Zero local links | Plain text fixture | Zero missing links |
| 4 | Boundary | One local link | Existing index link | One valid destination |
| 5 | Boundary | Several local links | Index plus architecture pages | Every destination checked |
| 6 | Equivalence | Relative file and directory | docs/README.md; docs/architecture/ | Both resolve |
| 7 | Equivalence | Anchor and external URL | Local section; HTTPS link | Not mistaken for missing files |
| 8 | Equivalence | Encoded local path | Space encoded as %20 in fixture | Decoded before lookup |

Numeric and runtime exception boundaries: N/A — no compiler operation changes. Readiness itself
has zero unmet prerequisites here. If one or several prerequisites become unavailable, record every
blocker with its owner and resolution evidence; this work order can no longer be Ready.

### Measurements

| Metric | How | Threshold | Source | Reported in |
| --- | --- | --- | --- | --- |
| Line coverage, whole solution | Existing coverage gate job | ≥ 95 % | ISSUES.md Test design | CI |
| Branch coverage, whole solution | Existing coverage gate job | ≥ 95 % | ISSUES.md Test design | CI |
| Acceptance scenarios | NUnit documentation fixture in full Release suite | 100 % pass | This example's contract | PR and CI |
| Missing tracked link targets | Documentation link check | Zero | This example's contract | Test output |
| Compiler contract partitions | N/A — documentation only | No support claim added | PRD §7.2 | PR scope |

### Evidence to attach

Changed paths, SDK version, positive and failing-fixture link-check output, build/test results,
coverage and CI links, signed commit verification, and maintainer review. Generated-code hashes,
foreign runtime results and performance measurements: N/A — no translated code or performance claim.

### Implementation plan

1. Claim the live work order and create its issue-numbered branch from current main.
2. Add the two feature scenarios and failing NUnit navigation checks, including the missing target.
3. Update README navigation without changing architecture contracts.
4. Run focused checks, Release build and full tests; record the measurements.
5. Sign commits, push, open a squash PR with the live issue's closing reference, and merge after
   green CI and review.

**Do not:** fabricate tracked targets, skip the failing link fixture, weaken coverage, alter product
semantics, or call a documentation change verified compiler support.

### Target state

`N/A — documentation/process only`. No compiler gate state is reached.

### Labels and milestone

`documentation`, `area:infrastructure`, `state:ready`; P0 — Repository, baseline and contracts.
The live issue would receive those labels and milestone; this hypothetical example creates nothing.

### Definition of Ready

- [x] Scoped imperative title and P0 reference.
- [x] Listed-role user story, in-scope and out-of-scope lists.
- [x] Available inputs; no unmet dependencies.
- [x] Repository fixtures pinned with licence; authoritative references identified.
- [x] Exact available SDK version; irrelevant foreign tools explained.
- [x] Changed files and absence of public API/diagnostic changes specified.
- [x] Concrete positive and negative Gherkin scenarios.
- [x] All four test-design axes covered or justified.
- [x] Measurements include commands/checks, thresholds, sources and reporting.
- [x] Test-first plan and forbidden shortcuts.
- [x] Documentation target state and one-PR scope.
- [x] Category, area, readiness and milestone specified; hypothetical review dated above.

## Blocked example

**Title:** `interop: preserve a C DLL call in a PowerBASIC binding`

**Review:** Hypothetical maintainer review, 2026-11-12; decision `state:blocked`.
This is intentionally incomplete and cannot be implemented as a ready work order. A missing or
unknown prerequisite is not an irrelevant field: unknown is not N/A.

### Why and intended scope

PRD §§17 and 29–32 require foreign calls and layout to preserve the original ABI. The desired first
slice is one function and one record, called by both a reference C caller and generated PowerBASIC.
No actual DLL or exact function has been supplied. No binding, target capability or diagnostic code
is claimed to exist.

### Missing prerequisites

| Missing input | Owner | Evidence to unblock |
| --- | --- | --- |
| PowerBASIC dialect: DOS or Windows | Requester | Named dialect and compatible target OS |
| Process bitness and CPU | ABI maintainer | Exact process architecture and DLL compatibility |
| calling convention and export identity | ABI maintainer | Export metadata and independently compiled C caller |
| DLL binary and header | Requester | Accessible binary/build and full source SHA/path with licence |
| Record layout, pointer lifetime, callbacks and failures | ABI maintainer | Sizes, offsets, alignment, ownership and observable-error contract |
| Independent oracle and toolchain | Verification maintainer | Working C caller, exact tool versions and captured expected behavior |

**Resolving issues:** not created in this hypothetical example. In a live issue, name the real
resolving issues before readiness; never invent issue numbers, pins or expected outputs.

### Acceptance direction, not an executable contract

A compatible binding must preserve export identity, arguments, layout, results and memory effects.
An incompatible ABI must be rejected explicitly. Concrete scenarios, diagnostic codes and fixture
values remain unspecified until the inputs above exist; that is why this example is Blocked.

### Readiness decision

- [ ] Exact inputs, callable function and independent oracle supplied.
- [ ] Target dialect, bitness and calling convention verified.
- [ ] Record layout and lifetime contract available.
- [ ] Positive and negative scenarios and all test-design partitions specified.
- [ ] Tool versions, measurements, labels/milestone and resolving issues reviewed.

One missing prerequisite prevents Ready; several missing prerequisites each need their own owner
and evidence. Applicable ABI facts need evidence, never bare N/A. After resolution, review again;
changes to a fixture hash or calling convention revoke the prior review before work continues.
Use `state:draft` for requirements that need refinement and `state:blocked` for unavailable external
inputs. The live category would be `enhancement`, area `area:interop`, milestone P3; no compiler gate
state is claimed while the contract is incomplete.
