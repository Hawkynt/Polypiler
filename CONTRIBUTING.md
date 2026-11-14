# Contributing

Thanks for helping improve **Polypiler**. This guide covers building, testing, and the conventions
every change is expected to follow. Coding agents must also read [`AGENTS.md`](AGENTS.md).

**Every change starts as an issue written to [`ISSUES.md`](ISSUES.md)** — how work is cut into issues,
the Definition of Ready (user stories, Gherkin acceptance criteria, test design, measured thresholds)
and the Definition of Done. The skeletons are in [`.github/ISSUE_TEMPLATE/`](.github/ISSUE_TEMPLATE/).

## Prerequisites

- A recent [.NET SDK](https://dotnet.microsoft.com/download) matching the `TargetFramework` in
  `Directory.Build.props`.
- `perl` and `node` are only needed to run the release/versioning scripts locally; CI provides them.
- Every external toolchain a test runs, and the version it expects, is pinned in
  [`tooling/toolchains.lock`](tooling/toolchains.lock). `polypiler-tooling toolchains probe` lists the
  core toolchains and how this machine compares (`--only node,dotnet-sdk` narrows it, `--format json`
  prints one object per line); `polypiler-tooling toolchains validate` checks the file itself. A test
  marked `[RequiresToolchain("<key>")]` is inconclusive when its toolchain is absent or at another
  version, and fails instead when `POLYPILER_REQUIRE_TOOLCHAINS=1`.

## Build

```bash
dotnet restore Polypiler.slnx
dotnet build Polypiler.slnx -c Release --no-restore
```

## Test

```bash
dotnet test Polypiler.slnx -c Release                         # everything
dotnet test Polypiler.Tests -c Release                        # the unit tests, which carry the coverage
dotnet test Polypiler.Tests.Acceptance -c Release             # the executable, node and the C# compiler
dotnet test Polypiler.slnx -c Release --filter "TestCategory!=Slow&TestCategory!=EndToEnd&TestCategory!=OsIntegration&TestCategory!=ExternalInterop&TestCategory!=PolyglotInterop&TestCategory!=Performance"   # the fast tier
```

Tests are [NUnit](https://nunit.org). New behaviour is test-first: add the failing test, then make it
pass. Keep test data deterministic (fixed seeds/strings) and generate it in setup rather than
committing large binary fixtures.

### Every test runs in parallel

Both test projects declare `[assembly: Parallelizable(ParallelScope.All)]` with
`FixtureLifeCycle(LifeCycle.InstancePerTestCase)`: every case gets its own fixture instance and runs
beside the others, one NUnit worker per logical processor. `Polypiler.Tests` runs with the server
garbage collector, because most of its tests allocate hard. What that asks of a test:

- **Share nothing mutable.** A working directory comes from the `[SetUp]` of the case. A cache is a
  `ConcurrentDictionary` of `Lazy` values, filled during the run and never written to disk — the
  compiled fixtures, the builds of `GeneratedCSharp`, the translations of the acceptance tests.
- **Process-wide state is the exception.** A test that swaps `Console.Out` is `[NonParallelizable]`,
  with a comment saying why; nothing else is.
- **One case per fixture and profile.** A test that loops over fixtures, profiles or millions of
  calls inside one method keeps the scheduler from spreading it and ends up as the last test of the
  run. It takes the fixture, the profile or a slice of the calls as an argument instead.

The acceptance tests start processes where a process is what they are about. A scenario of a
feature file runs the real `polypiler`, and so does every test of a command-line behaviour. A test
that only needs the generated file translates with `Toolchain.Translate`, which calls
`Program.Run` — what the executable's `Main` calls — in process; `InProcessTranslationTests` holds it
to the executable's exit code, text and files for every kind of command line the tests use. Generated
JavaScript runs in a `NodeWorker`: one long-lived `node` per test thread that loads a module afresh
for each request. A crash, a hang past 60 seconds or an answer outside its protocol fails that one
test and replaces the worker. A script that declares a global object, and a test about `node`
itself, still gets a `node` of its own.

Measured on a laptop with 16 logical processors that was busy with other work at the same time
(load average about 30), Release, `--no-build`:

| Project | Before | After |
|---|---|---|
| `Polypiler.Tests` | 8,265 tests, one at a time, 203 s | 8,706 cases, 67 to 75 s |
| `Polypiler.Tests.Acceptance` | 955 tests, 385 s | 2,319 cases, 134 to 137 s |

The same checks run as before; the cases are more because loops became cases.

### Generated C# is compiled for the framework it names

The C# backend writes for a target profile, from .NET Framework 2.0 with C# 2 to .NET 10 with C# 14.
The tests build every generated file twice: for the running runtime, to call it, and in the profile's
language version against the **reference assemblies of the profile's framework**, with warnings as
errors. Those come from NuGet packages that `Polypiler.Tests/Prototype/Support/ReferenceAssemblies.props`
downloads without referencing them (`Microsoft.NETFramework.ReferenceAssemblies.net20`/`net35`/`net46`/`net48`,
`NETStandard.Library`, `NETStandard.Library.Ref`, `Microsoft.NETCore.App.Ref`; all MIT, test-only), so
`dotnet restore` needs them once. A profile whose package is missing fails its tests with the name of
the package; it is never compiled against the running runtime instead. A new profile is a new line in
that file and in `CSharpProfile`.

### The two tiers

A test belongs to the **fast tier** unless it says otherwise. That direction is deliberate: marking
every fast test would mean tagging thousands of them and remembering to tag each new one, and the
one somebody forgets silently drops out of the fast tier without anybody noticing. Opting out is a
decision somebody makes once, in the open.

A test opts out by carrying one of these categories:

| Category | For |
|---|---|
| `Slow` | Quick in kind, expensive in practice — a large fixture, a long deadline, a wall-clock wait. The general escape hatch. |
| `EndToEnd` | Drives the built artifact, a mount, or a real external tool end to end. |
| `OsIntegration` | Needs something of the host: a driver, a service, elevated rights, a display. |
| `ExternalInterop` | Checks our output against a third-party tool (7-Zip, zstd, flac …). |
| `PolyglotInterop` | Round-trips through another language ecosystem. |
| `Performance` | Asserts on wall-clock or throughput. Advisory: a shared runner must never turn a timing assertion into a red pull request. |

**Opting out defers a test, it never skips one.** Everything runs on the pull request. The tiers
decide *when*, not *whether*.

**Write `TestCategory!=`, never `Category!=`.** They select the same tests and report the same
results, and only one of them actually skips anything: with `Category!=` the adapter filters the
REPORTING and still executes the excluded fixtures. Measured on a 5,489-test suite, same assembly,
same attributes:

```
--filter "Category!=Slow"        5155 selected, 5143 passed, 12 skipped   5m17s
--filter "TestCategory!=Slow"    5155 selected, 5143 passed, 12 skipped     52s
```

Six times the cost for an identical answer. A fast tier written with `Category!=` looks correct in
every way except the clock, which is the one thing it exists for.

**Filter by category, never by `FullyQualifiedName`, once a suite is large.** With the NUnit3 VSTest
adapter, an FQN filter selecting more than roughly two thousand tests makes the adapter **execute the
whole assembly and discard the results of the tests the filter excluded**. Measured on a 27,555-case
suite: a 3,707-case FQN selection charged 183s of test time and took **870s of wall clock**, with 681s
of it in pauses no test duration accounts for. Stack samples of the test host during those pauses
showed it running tests that were not in the selection and had no entry in the results file. A
`Category` filter does not do this at any size — the same suite filtered by category runs 22,342 cases
in 215s with no gaps at all.

The practical rule: shard by category or by assembly. An FQN shard of a large suite costs more than
running everything.

Two rules that make the difference real:

- **A test in the fast tier finishes in well under a second.** If it does not, either make it so or
  tag it `Slow`. A fast tier that creeps up to twenty minutes stops being read, and then people
  open pull requests to find out whether their code compiles — which runs the expensive tier ten
  times per change instead of once.
- **Measure over a window long enough to mean something.** A test that divides CPU time by
  wall-clock, counts allocations per operation, or reads any other rate needs a sustained sample.
  Over 90 ms the reading is thread start-up and tiered JIT, not the thing being measured; that is
  how a four-core runner once reported 6.2 cores busy. Run such a test to a *duration*, not to a
  fixed iteration count, so the window holds on a fast machine and a slow one alike — and tag it
  `Slow`, because now it is.

## Commit conventions

- One concern per commit, with a descriptive body.
- Subject lines start with a prefix — `+` added · `-` removed · `*` changed · `#` bug fixed ·
  `!` critical todo. Never begin with "fix"/"changed"/"modified".
- Write everything as if authored by hand: no AI attribution anywhere.

## Code style

- K&R braces (opening brace on the same line, `} else` and `} catch` on the closing brace's
  line), 2-space indent (C#), file-scoped namespaces, `_camelCase` private fields, `this.`
  qualification, XML docs on public members, LF endings.
- `Nullable` and `ImplicitUsings` are enabled centrally in `Directory.Build.props`.

## GUI screenshots

GUI applications treat screenshots as generated product documentation. The README/docs should show
**all primary dialogs and top-level windows that represent distinct workflows**, not just whatever
window appears at startup. Typical candidates are the main window, settings/preferences,
import/open/add flows, editors/configuration dialogs, export/save/publish flows, previews/results,
reports, and substantial wizards. Do not multiply screenshots for trivial confirmation boxes or
visually identical variants.

One of them is the hero shot and goes directly under the README's pitch, with no heading — it is the
image that decides whether a reader keeps reading. The rest are the tour, under `## 🖼️ Screenshots`.
A repo that sets `repo-readme-gui: true` on the shared workflow has both checked.

Each documented surface needs an application-owned demo scenario. Prefer a hidden/documentation-only
startup option such as `--screenshot-demo=<scenario>:<output>` or an equivalent internal entry point
that opens the real UI in a deterministic state and writes the image without operator interaction.
The exact mechanism is project-specific; these properties are not:

- Build the scenario from the application's real domain models, presenters/view-models and controls.
  Do not draw fake table rows over the UI, stitch screenshots, or maintain a parallel mock screen.
- Populate enough plausible data to make the screenshot useful: multiple representative items,
  meaningful names and values, different statuses, optional fields, edits/warnings where relevant,
  and edge cases worth seeing. An empty dialog is technically reproducible and practically useless.
- Keep it deterministic and private: fixed values/seeds/timestamps, no personal data, no live network
  or cloud dependency, and no dependency on locally installed third-party tools when equivalent
  in-memory/pre-parsed data can drive the same production UI.
- Make scenarios independently addressable so CI can capture each primary dialog/window directly.
  Adding a new primary surface means adding its scenario and screenshot in the same change.
- Store generated images under a descriptive path such as `screenshots/` or `docs/screenshots/`, use
  descriptive kebab-case filenames, reference them from README/docs with useful alt text, and keep
  the surrounding text authoritative — screenshots complement documentation rather than replacing it.

`generate.yml` should regenerate every committed screenshot on a working-branch push and commit the
changed files through `Hawkynt/RepositoryTemplate/commit-generated-file@v1`. The generation job should
also sanity-check that each expected image exists and is a plausible image before committing it.

## Continuous integration

Workflows live in `.github/workflows/`:

| Workflow | Trigger | Purpose |
|---|---|---|
| `smoke.yml` | push to a working branch | The fast tier. One OS, fast tests only, no coverage, no package-README check. Minutes. |
| `generate.yml` | push to a working branch | Refreshes derived files (screenshots, references) and commits them onto the branch. |
| `ci.yml` | PR to `main` | The gate. Every OS, every category, coverage. |
| `_build.yml` | called by release/nightly | Shared build/pack step so both paths produce identical artifacts. |
| `nightly.yml` | automatically after green CI on `main` | Dated `nightly-YYYYMMDD` prerelease, GFS-pruned. |
| `release.yml` | manual dispatch | Cuts a dated `vYYYYMMDD` release; publishes NuGet packages when configured. |

### Fast on push, comprehensive on the pull request

```
push to a working branch  ->  smoke.yml + generate.yml     fast, one OS, derived files refreshed
pull request              ->  ci.yml                       the gate: every OS, every category
push to main              ->  nothing                      the pull request was already green
```

Both branch-push workflows call shared reusable workflows, so a repo declares intent rather than
copying a matrix:

```yaml
# .github/workflows/smoke.yml
jobs:
  smoke:
    uses: Hawkynt/RepositoryTemplate/.github/workflows/dotnet-smoke.yml@v1
    with:
      solution: Polypiler.slnx
```

Three properties of this split are not negotiable:

- **Every job declares `timeout-minutes`.** Without one a job inherits GitHub's six-hour default, so
  anything wedged — a hung mount, a deadlocked pump, a UI waiting on a window that never opens —
  holds a runner for six hours and queues every other job behind it. Size it at roughly twice the
  observed successful run. The shared workflows take `timeout-minutes` and
  `coverage-timeout-minutes` inputs and default to 60 and 120.
- **Coverage is a reporting metric, not a gate.** Instrumentation costs several times the test run.
  Once it exceeds what a pull request can wait for, set `coverage: false` on the shared workflow and
  measure on a schedule instead, in a workflow that is *not* `cancel-in-progress` so it can actually
  finish. Coverage that never completes before the next push supersedes it reports nothing while
  blocking everything — that is not a stricter gate, it is an unread one.
- **Every category has somewhere it runs.** A category excluded from the main filter needs its own
  step, or its tests execute nowhere. This is easy to get wrong by omission and invisible when you
  do: one repo excluded `ExternalInterop` from the core filter and never added the step, so 17 files
  and ~167 cases — the ones checking our output against real 7-Zip, zstd and flac — never ran on any
  runner. After changing a filter, list the categories and confirm each appears in some step.

Versions come from files, not git tags: the shared `stamp-version` action stamps each manifest with
its own folder's commit count, so sibling packages version independently, while the repo-level marker
is simply the date (`vYYYYMMDD` / `nightly-YYYYMMDD`). The versioning, changelog and prune scripts
live in `Hawkynt/RepositoryTemplate` and reach this repo through composite actions — there is no
`scripts/` directory here to keep in sync.

To validate workflow edits, [`actionlint`](https://github.com/rhysd/actionlint) is the recommended
linter:

```bash
actionlint .github/workflows/*.yml
```

### When the package-README check fails

The check regenerates each package's README and `REFERENCE.md` from the built assembly plus its XML
docs, and compares. Two things about it are worth knowing before you try to debug one locally.

**The generated output depends on the SDK.** CI installs a preview SDK (`dotnet-quality: preview`),
which emits XML documentation for C# `extension(T)` members; a stable SDK does not. So a reference
regenerated on a stable SDK loses those summaries, and one regenerated on CI gains them — the same
source, the same command, two different correct answers. A check that passes locally can fail on CI
for that reason alone, and a stale reference can ship because the author's SDK could not see the
difference.

**Take CI's output, do not rebuild.** The action uploads the regenerated files as a
`package-readmes` artifact whenever the check fails, precisely so a red build is a download rather
than a debugging session:

```bash
gh api "repos/OWNER/REPO/actions/runs/<run-id>/artifacts" \
  --jq '.artifacts[] | select(.name=="package-readmes") | .id'
gh api "repos/OWNER/REPO/actions/artifacts/<id>/zip" > readmes.zip
```

Diff before committing, and diff with `--strip-trailing-cr`. The check may run on a Windows runner,
where the generator writes CRLF; against LF files in git that shows every line as changed and hides
whether anything real moved.

### When the repo-README check fails

Nothing above applies. This one reads one file and generates nothing, so there is no artifact to
fetch, no SDK to match and no line endings to argue with — reproduce it in full with one command:

```bash
curl -sL https://raw.githubusercontent.com/Hawkynt/RepositoryTemplate/v1/scripts/repo-readme.mjs -o /tmp/repo-readme.mjs
node /tmp/repo-readme.mjs --check . --repo OWNER/REPO
```

Every finding names the rule that produced it and the line it sits on, and CI reports them as
annotations on the diff. `--write` fixes the badge block; the rest are prose decisions, which is why
they are left to a person. The rules and the canonical order are in
[`repo-readme/README.md`](https://github.com/Hawkynt/RepositoryTemplate/blob/main/repo-readme/README.md).

## Releases

Stable releases are cut manually by the maintainer:

```bash
gh workflow run release.yml
```

Never cut a release unless explicitly asked.
