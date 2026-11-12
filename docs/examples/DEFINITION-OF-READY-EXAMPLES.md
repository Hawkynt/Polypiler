# Worked Definition of Ready examples

These are **hypothetical issue bodies**, not live issues, implementations or test results. The [DoR policy](../DEFINITION-OF-READY.md) is normative and the [PRD](../PRD.md) controls Definition of Done.

## Example A: Ready — P0 ADR index and template

**Title:** P0: Add an architecture decision record index and template

**Readiness:** Ready (illustrative reviewer decision)

**Category:** Documentation/process

**R01 — Why and reference.** PRD §§8 (P0), 15 (architectural decisions) and 33.3. Engineers need to record alternatives and consequences for compiler design changes; otherwise inconsistent decisions can silently conflict with the PRD.

**R02 — One-PR scope.** Add `docs/adr/README.md` (numbering, states, links and PRD reconciliation policy), `docs/adr/0000-template.md` (context, constraints, considered options, decision, consequences, verification/rollback), and a link to the index from `docs/README.md`. Do not alter compiler code or existing normative PRD language.

**R03 — Dependencies.** Existing `docs/PRD.md` and `docs/README.md` on `main`; Markdown editing and Git. Both documents exist. No external artifact, SDK, upstream license or prior implementation is required. Work on a feature branch with reviewed PR.

**R04 — Source/target profile.** N/A — this task creates original architecture documentation, not translated or executed code. It has no source dialect, runtime, CPU or ABI.

**R05 — Semantic contract.** Do not silently override the PRD; the ADR template must refer to the affected normative requirements and require reconciliation if incompatible. IR arithmetic, memory effects and runtime semantics: N/A — no compiler operation is modified.

**R06 — Provenance and oracle.** N/A — no external code or test corpus is imported. The in-repo PRD is the authoritative reference, and the outcome is verified by link/structure review rather than a target-language oracle.

**R07 — Observable acceptance:**

- [ ] `docs/adr/README.md` explains ADR ID allocation, lifecycle and the relationship to the PRD.
- [ ] `docs/adr/0000-template.md` provides headings for context, alternatives, decision, consequences, verification and rollback.
- [ ] `docs/README.md` contains a valid relative link to the new ADR index.
- [ ] Documentation makes no false implementation claims; changes are proposed on a feature branch and reviewed.

**R08 — Positive and negative scenarios:**

~~~gherkin
Scenario: A developer prepares a new architectural decision
  Given the ADR index and template exist
  When the developer follows the template
  Then the context, alternatives, decision and consequences are captured
  And the index describes how to link the resulting record

Scenario: An ADR would contradict an existing PRD requirement
  Given a proposed ADR changes a normative semantic invariant
  When a contributor follows the ADR policy
  Then the contributor must reconcile the affected PRD section
  And the ADR is not treated as an implicit replacement for the PRD
~~~

**R09 — Test plan.** Inspect both new pages and the docs index on GitHub; resolve all local Markdown links to tracked paths; check the headings against the acceptance criteria; compare the ADR precedence statement against PRD §§15 and 33.3. `dotnet build` and `dotnet test`: N/A — documentation-only and no solution/test project currently exists in this scope.

**R10 — Risks.** Documentation drift; mitigate with a PRD reconciliation link. Security, machine effects, source copyright, performance and external ABI: N/A — no executable or third-party artifacts are introduced.

**R11 — Ownership and size.** One documentation PR. Implementer: issue assignee; readiness decision and final review: repository maintainer or nominated human reviewer.

**R12 — Decision.** Ready once the designated reviewer validates the statements above and records their identity/date in the live issue. There are no prerequisites to guess. **Nothing has been implemented or tested merely because this example is Ready.**

**Why Ready?** The exact deliverables, document contracts, checks, failure case, prerequisites and reviewer are known and independently assessable.

---

## Example B: Blocked — C DLL binding to PowerBASIC

**Title:** P3: Generate PowerBASIC bindings for C DLL declarations

**Readiness:** Blocked (illustrative reviewer decision)

**Category:** ABI/runtime/interop

**R01 — Why and reference.** PRD §§17, 29 and 31–32. Generate native PowerBASIC external declarations with equivalent ABI behavior instead of copying/reimplementing foreign DLL code.

**R02 — Intended scope.** Import a real C header and external library, emit `DECLARE ... LIB ... ALIAS` declarations or verified adapters, and compare actual calls. The exact DLL and first-function slice are **not identified**.

**R03 — Dependencies.** Requires access to a real compiled DLL and import/export metadata, source header at a pinned revision, compatible PowerBASIC compiler/runtime, and C reference caller. None has been supplied.

**R04 — Profiles. BLOCKED:** PowerBASIC DOS versus Windows is unknown; compiler version, process bitness, target CPU/OS and calling convention are unknown. A DOS OMF library is not interchangeable with a Windows DLL.

**R05 — Semantic contract. BLOCKED:** Parameter by-value/by-reference layout, structure packing/alignment/stride, pointer ownership, callbacks, and observable errors/effects are unspecified.

**R06 — Provenance. BLOCKED:** No pinned header, library build, reference caller, known-answer corpus or license/redistribution analysis exists.

**R07 — Candidate acceptance (not yet executable):**

- [ ] Correct native export identity and calling convention are retained.
- [ ] A packed record has the exact size, offsets, alignment and array stride expected by the real C callee.
- [ ] Actual call and out-buffer mutation match a C caller.
- [ ] Unsupported bitness/by-value ABI produces a precise diagnostic, never a silently incompatible declaration.

**R08 — Proposed behavior scenarios:**

~~~gherkin
Scenario: A compatible foreign import is translated
  Given a pinned C DLL export and a fully specified PowerBASIC for Windows target
  When the importer generates its binding and required packed record
  Then the actual DLL call observes the original arguments and memory layout
  And its results and effects match the independently executed C caller

Scenario: An unsupported foreign ABI is requested
  Given a target runtime incompatible with the DLL architecture or calling convention
  When the compiler legalizes the foreign call
  Then it reports an unsupported ABI or bridge requirement
  And it does not emit a callable declaration with incorrect semantics
~~~

**R09 — Candidate verification.** Run `sizeof`/`offsetof`/stride tests compiled with the original C toolchain, then execute calls from the chosen PowerBASIC environment with positive and negative fixtures. The commands and test artifacts cannot yet be specified because the tools and DLL are unknown. **No tests are claimed to pass.**

**R10 — Risks.** Memory corruption or stack imbalance from mismatched ABI, unsupported cross-bitness loading, undocumented pointer ownership, missing redistribution rights. Dependency owner: issue requester / designated ABI maintainer.

**R11 — Size/ownership.** Scope down to one callable function and one packed structure when inputs exist. Implementer to be assigned; human maintainer to review dependency closure and approve readiness.

**R12 — Decision: Blocked.** These correctness-critical facts must not be replaced with `N/A` or guessed conventions. A reviewer can only approve Ready after the following are resolved:

- [ ] State PowerBASIC **dialect/compiler/version**, OS/architecture, calling convention and runtime.
- [ ] Pin a real C header, DLL/import library and compatible reference toolchain.
- [ ] Supply export identity and a working independent C caller; document license permissions.
- [ ] Specify all observable layout, ownership, error, callback and effect contracts.
- [ ] Reduce the first PR to an exact verified slice plus failure test; re-evaluate R01–R12.

**Why Blocked?** The missing artifacts and ABI contract determine whether any generated call can be correct; starting implementation would require making up requirements.
