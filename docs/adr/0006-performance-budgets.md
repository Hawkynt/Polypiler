# ADR 0006 — Performance budgets

Area: foundation

| | |
| --- | --- |
| Status | accepted |
| Index | [decision records](README.md) |
| Date | 2026-11-14 |
| Issue | [#22](https://github.com/Hawkynt/Polypiler/issues/22) |
| Requirements | [PRD §4.8](../prd/02-core-architecture.md#s4-8), [§13.2](../prd/04-roadmap-and-delivery.md#s13-2), [§23.5](../prd/06-recovery-acceptance-and-gates.md#s23-5) |
| Budgets | [`tooling/bench/budgets.yaml`](../../tooling/bench/budgets.yaml) |
| Raw numbers | [`docs/benchmarks/baseline-p0/`](../benchmarks/baseline-p0) |
| Evidence | [`evidence/0006/`](evidence/0006) |

## Question

What regression thresholds does CI enforce for execution time, allocations, peak memory, generated
source size, binary size, build time and compile cost — per metric and per `--opt` objective — given
the run-to-run variance actually measured? Until this record, every performance row read
`record only, no gate`.

## Decision

1. **A metric is gated only when it repeated exactly in every run of the baseline series, and then at
   no increase** (option 3). Today that is `sourceBytes` of the three Cipher baselines and
   `allocatedBytes` of `cli-startup`: `maxRegressionPercent: 0`. A larger value is `PLY0073` unless the
   change re-baselines it ([Re-baselining](#re-baselining)).
2. **Wall time, processor time and build time are record only** on every host the project has. The
   median of a 30-run series moved by up to 3.8× between series of identical code on the measuring
   host, and by 2.6× (build) and 3.2× (test) between CI runs; no threshold keeps false alarms rare and
   still catches a 25 % slowdown ([Replay](#replay)).
3. **Peak memory is record only for now**, with a candidate budget of **25 %** for the `process`
   benchmarks: on the measuring host it gave 0.1 % false alarms over 5 850 pairs and caught a 25 %
   increase in half of them. It is not switched on because the runner on `main` still misses the
   memory of short programs (fixed by [#1347](https://github.com/Hawkynt/Polypiler/issues/1347), not
   merged) and because no CI host has been measured ([Follow-ups](#follow-ups)).
4. **No `maxRelativeMad`.** It only decides whether a wall-time pair is compared, and wall time is not
   gated.
5. **No dedicated runner** (option 4) for now: the repository has none (the runners API lists 0
   self-hosted runners), and the deterministic gates do not need one.
6. **Per objective** the rule is the same, per metric: each `--opt` variant of a benchmark gets the
   budgets of the metrics its objective is judged by, as rows of its own variant name.

| Objective | Metrics that judge it | Gate today |
| --- | --- | --- |
| `size` | `sourceBytes`, `binaryBytes` | 0 % on every variant whose series repeated them exactly |
| `memory` | `peakRssBytes`, `allocatedBytes`, `allocations` | `allocatedBytes` 0 % where exact; `peakRssBytes` record only (candidate 25 %) |
| `speed` | `wallNs`, `cpuNs` | record only |
| `balanced` | all of the above | the union of the gates above |

No benchmark has `--opt` variants yet: the optimizer does not exist at P0. The table is what the issue
that adds the first such variant applies, after measuring it.

## Budgets per metric

| Metric | PRD §13.2 name | Reproducible between runs | Rule | Threshold | Evidence row |
| --- | --- | --- | --- | --- | --- |
| `sourceBytes` | generated source size | yes: 11 914, 14 929, 44 485 bytes in all 26 series | exact | 0 % | [Determinism](#determinism) |
| `binaryBytes` | binary size | not measured: no P0 benchmark builds a binary | exact once a series shows it repeats | none yet | — |
| `allocatedBytes` | compilation cost (in process) | yes: 1 016 bytes per operation in all 10 runs of `cli-startup` | exact | 0 % | [Determinism](#determinism) |
| `allocations` | allocation count | not measured: BenchmarkDotNet reports bytes only | exact once measured | none yet | — |
| `peakRssBytes` | peak memory | no: RSD of the series medians up to 7.4 % | relative, candidate 25 % | record only | [Replay](#replay) |
| `wallNs` | execution time | no: series medians up to 3.8× apart | — | record only | [Replay](#replay) |
| `cpuNs` | execution time (processor) | no: quantised to 10 ms ticks, up to 5× apart | — | record only | [Replay](#replay) |
| `buildNs` | build time | no: CI build step 6–45 s | — | record only | [Compile-time baseline](#compile-time-baseline) |

PRD §23.5's further metrics (instruction counts and the like) arrive through `extra`; each follows the
first row's rule: exact when a series shows it repeats, record only otherwise.

## Re-baselining

`bench compare` judges the current results against a baseline result set of the same environment
(same operating system, CPU, toolchain versions; `PLY0072` otherwise), which CI produces from the
target branch's commit. A change that grows a gated metric on purpose — a fixture replaced by a newer
upstream commit, a feature that costs allocations — raises that row's `maxRegressionPercent` in
`tooling/bench/budgets.yaml` to the measured increase, rounded up to the next whole percent, in the
same pull request, and its commit body says why and cites this record. Once that change is the
baseline, the next change touching the file sets the row back to 0. A metric is moved from
record only to gated, or its rule changed, only by a new measured series and an amendment of this
record that cites it.

## Evidence

### What was measured

- **Source-language baselines** (deciding evidence (a) of the issue): Adler-32, Fletcher-32 and
  CRC-32-IEEE of Cipher at `Hawkynt/Hawkynt.github.io@b3a8a8e92cbca83475986391c3cac56f0b185d29`
  (LGPL-3.0-or-later, [ADR 0030](0030-cipher-source-licence.md)), the files kept byte for byte under
  [`tests/fixtures/bench/cipher/`](../../tests/fixtures/bench/cipher), run by
  [`cipher-checksum.js`](../../tests/fixtures/bench/cipher-checksum.js) under `node` 22.23.3 over a
  deterministic message of 1 KiB, 64 KiB and 1 MiB. The definitions are
  [`tests/benchmarks/cipher-*.bench.yaml`](../../tests/benchmarks): one warm-up and 30 measured runs
  per variant, each through the sandboxed runner. A run counts only if it prints `ok`, which it does
  only for the checksum [`oracle.py`](evidence/0006/oracle.py) computes without Cipher (zlib and
  Fletcher's definition; [`oracle.txt`](evidence/0006/oracle.txt)).
- **Compile-time baseline** (deciding evidence (b)): the solution's clean and no-op builds, the fast
  test tier of `Polypiler.Tests`, and `cli-startup` (BenchmarkDotNet in process); plus the Restore,
  Build and Test steps of the last 40 green CI runs on `ubuntu-latest` and `windows-latest`.
- **Host**: one developer machine, Linux 7.2.6, Intel Core i9-11950H, 16 logical cores, shared with
  other build and test work. Its **1-minute load average was 30–65 throughout** (2.5–4 runnable tasks
  per core); it is recorded at the start of every series and of every benchmark in the series
  (`load1AtStart`, `load5AtStart` in `series-*.csv`). No quiet window occurred, so the numbers are
  those of a heavily shared host, which is what a shared CI host is too, but they say nothing about a
  quiet one.

[`measure.sh`](evidence/0006/measure.sh) runs the series, [`analyse.py`](evidence/0006/analyse.py)
computes every statistic and replay below and writes the CSV files under
[`docs/benchmarks/baseline-p0/`](../benchmarks/baseline-p0); `analysis.txt` is its output.

### The memory the runner missed before #1347

The runner on `main` (`c31b499`) read the whole process table at every 20 ms sample; on a loaded host
one read took longer than a short program lives, so the sample saw bubblewrap's 3.4 MB instead of
node's ~50 MB, and 0–2 ms of processor time. [#1347](https://github.com/Hawkynt/Polypiler/issues/1347)
(branch `1347-flaky-under-load`, commit `bd6494b`) samples the process tree instead. The series
were therefore measured with both runner builds:

| Runner | Series | Samples | Peak memory below 16 MiB (missed) | Largest ratio of two series medians, peak memory |
| --- | ---: | ---: | ---: | ---: |
| `main` (`c31b499`), "old" | 3 | 810 | 127 (15.7 %) | 11.8× |
| `1347-flaky-under-load` (`1c7de25`), "fixed" | 26 | 7 020 | 0 | see below |

Only the fixed runner's series decide anything below; the old runner's are kept
(`samples-old.csv`, `series-old.csv`) as the record of the defect. The numbers of both were made from
the same benchmark definitions and the same commit of this repository; only the runner build differs.

### Statistics

Fixed runner, 26 series of 30 runs, 9 variants: 780 samples per variant and metric. The ranges are
over the 9 variants; every row per variant is in [`analysis.txt`](evidence/0006/analysis.txt) and
the numbers it was computed from in `samples-fixed.csv` and `series-fixed.csv`. Deviations are
absolute, from the pooled median, relative to it; r is Pearson's correlation of a series' median
with the load average at its start.

| Metric | Pooled RSD % | p95 deviation % | p99 deviation % | RSD of the series medians % | Largest ratio of two series medians | r(load, median) |
| --- | --- | --- | --- | --- | ---: | --- |
| `wallNs` | 38–52 | 78–147 | 164–311 | 30–40 | 3.78 | 0.25–0.56 |
| `cpuNs` | 30–66 | 50–300 | 60–300 | 28–51 | 5.00 | 0.21–0.50 |
| `peakRssBytes` | 1.0–9.2 | 2.7–19.1 | 3.4–21.9 | 0.2–7.4 | 1.27 | −0.38–0.52 |

| Variant | Median wall time, ms (series medians) | Median peak memory, MB | Median processor time, ms |
| --- | --- | ---: | ---: |
| Adler-32 1 KiB / 64 KiB / 1 MiB | 54.7 (38.9–146.9) / 59.7 (43.2–141.7) / 89.0 (65.9–183.8) | 51.3 / 52.7 / 68.4 | 10 / 20 / 50 |
| Fletcher-32 1 KiB / 64 KiB / 1 MiB | 54.8 (39.3–129.5) / 65.0 (44.4–115.5) / 79.9 (60.0–195.7) | 51.3 / 59.3 / 69.0 | 10 / 30 / 50 |
| CRC-32 1 KiB / 64 KiB / 1 MiB | 56.5 (41.2–119.4) / 59.7 (43.8–140.2) / 74.3 (55.8–187.4) | 51.8 / 53.1 / 68.7 | 10 / 20 / 40 |

Node's start dominates: 1 KiB and 64 KiB differ by less than the noise, and even 1 MiB adds only
20–35 ms. **Not stable across windows**: between the first window (19:48–19:56, load 39–63) and the
second (20:00–20:04, load 30–47) the median of the series medians fell by 22–29 % for wall time and
by up to 67 % for processor time; peak memory moved by −15 % to +11 % for 1 KiB and 64 KiB (the
20 ms sampler catches node's heap at different moments) and by at most 0.2 % for 1 MiB.

### Determinism

`sourceBytes` was identical in every result of both runners (26 + 3 series of 9 variants): 11 914
(Adler-32), 14 929 (Fletcher-32), 44 485 (CRC-32). `allocatedBytes` of `cli-startup` was 1 016 bytes
per operation in all 10 runs, whose wall time per operation ranged from 1 740 to 3 780 ns (2.2×; [`cli-startup.csv`](../benchmarks/baseline-p0/cli-startup.csv)). Every other metric
varied (table above).

### Replay

The gate compares medians of series, so the replay compares the median of every series with that of
every other series of the same benchmark and variant (all ordered pairs, identical code): a ratio above
the threshold is a false alarm. The same pair with the current samples multiplied by 1.05, 1.10 and
1.25 is an injected slowdown, detected when the ratio exceeds the threshold. Fixed runner, 26 series,
9 variants, 5 850 pairs per metric (from [`analysis.txt`](evidence/0006/analysis.txt)):

| Metric | Largest ratio | Threshold | False alarms | +5 % detected | +10 % detected | +25 % detected |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `wallNs` | 3.78 | 10 % | 39.9 % | 43.8 % | 50.0 % | 61.7 % |
| `wallNs` | | 25 % | 31.8 % | 35.0 % | 38.3 % | 50.0 % |
| `wallNs` | | 100 % | 5.9 % | 8.1 % | 10.3 % | 14.8 % |
| `cpuNs` | 5.00 | 25 % | 29.6 % | 31.9 % | 32.9 % | 33.2 % |
| `cpuNs` | | 100 % | 11.1 % | 19.1 % | 19.3 % | 23.3 % |
| `peakRssBytes` | 1.27 | 10 % | 13.8 % | 17.4 % | 49.9 % | 91.7 % |
| `peakRssBytes` | | 20 % | 0.4 % | 7.5 % | 14.7 % | 82.4 % |
| `peakRssBytes` | | **25 %** | **0.1 %** | 0.5 % | 8.3 % | 49.9 % |
| `peakRssBytes` | | 50 % | 0.0 % | 0.0 % | 0.0 % | 0.4 % |

Option 1 (`baseline × (1 + k × σ_rel)`) and option 2 (largest deviation, rounded up) both land in the
last rows: a threshold that keeps false alarms under a few percent is far above any slowdown worth
catching. For wall and processor time no row does both, so neither metric is gated. For peak memory
the 25 % row is the candidate.

### Compile-time baseline

On the measuring host ([`compile-local.csv`](../benchmarks/baseline-p0/compile-local.csv); build
servers warm, packages restored, load average 41–57):

| Step | Runs | Min | Median | Max |
| --- | ---: | ---: | ---: | ---: |
| `dotnet build Polypiler.slnx -c Release --no-incremental` | 5 | 14.4 s | 15.2 s | 18.3 s |
| the same, nothing changed | 5 | 1.3 s | 1.9 s | 3.1 s |
| `dotnet test Polypiler.Tests -c Release --no-build`, fast tier (11 127 tests) | 3 | 31.5 s | 32.2 s | 36.9 s |
| `cli-startup` per operation (BenchmarkDotNet) | 10 | 1 740 ns | 1 886 ns | 3 780 ns |

On the CI hosts, the last 40 green runs of `CI` (2026-11-13 and -14; [`ci-build-test.csv`](../benchmarks/baseline-p0/ci-build-test.csv),
[`ci_durations.py`](evidence/0006/ci_durations.py)):

| Job | Step | Runs | Min | Median | Max | RSD | Max / median |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `ubuntu-latest` | Restore | 40 | 2 s | 13 s | 24 s | 46 % | 1.85 |
| `ubuntu-latest` | Build | 40 | 6 s | 18 s | 45 s | 54 % | 2.57 |
| `ubuntu-latest` | Test | 40 | 1 s | 395 s | 1 139 s | 80 % | 2.88 |
| `windows-latest` | Restore | 39 | 11 s | 31 s | 55 s | 42 % | 1.77 |
| `windows-latest` | Build | 39 | 9 s | 21 s | 44 s | 37 % | 2.10 |
| `windows-latest` | Test | 39 | 6 s | 468 s | 1 507 s | 82 % | 3.22 |

Build and test time are therefore record only. The compile cost that does repeat is
`allocatedBytes`, gated at 0 % for `cli-startup`.

### Option 4, a dedicated runner

`gh api repos/Hawkynt/Polypiler/actions/runners` lists 0 self-hosted runners, so its variance could
not be measured. The cost is a machine kept idle except for benchmarks, its maintenance, and a CI job
that queues on it. The deterministic gates of this record do not need it; timing does, and the
follow-up below decides whether it is worth it once a translated pair gives timing something to
protect.

## Options considered

1. **Relative threshold from measured noise.** Rejected for time: σ_rel of the series medians was
   up to 40 % (wall) and 51 % (processor), so any k that keeps false alarms rare gives a threshold above
   100 %. Kept as the shape of the peak-memory candidate.
2. **Fixed percentage per metric from the largest observed deviation.** The same numbers: 3.8× for
   wall time. Rejected for time and memory as a gate, used for the candidate's rounding.
3. **Deterministic metrics gated exactly, timing recorded.** Chosen.
4. **Dedicated hardware.** Not now; no runner exists.

## Consequences

- `tooling/bench/budgets.yaml` holds the nine `sourceBytes` rows of the Cipher baselines. It also holds the `allocatedBytes` row of `cli-startup`, which a comparison judges as soon as both result sets contain that benchmark.
- Every optimization issue's performance row cites this record: `record only` for time, `0 %` for the
  size and allocation metrics its benchmark repeats exactly.
- The runner's memory sampling must be the fixed one before a memory budget is switched on.

## Follow-ups

Drafted here, to be filed as issues:

1. **`infra: run the benchmarks in CI and compare with the target branch`** — a job on `ubuntu-latest`
   and `windows-latest` that runs `bench run --all` on the base and the head commit on the same runner
   and `bench compare --budgets tooling/bench/budgets.yaml`; it measures the CI host classes, which
   this record could not, with 30 runs per variant and the load average recorded, and decides the
   peak-memory candidate once #1347 is on `main`.
2. **`spike: re-measure the budgets once the first translated pair exists`** (P2 backends) — the same
   series for generated code (`binaryBytes`, build time, wall time of translated Cipher), and whether
   timing then justifies option 4.
3. A dedicated runner is not chosen, so no issue for it; follow-up 2 reopens the question.

## Limits of the evidence

- **One host, one evening, under heavy load.** 26 series in two windows (19:48–19:56 and
  20:00–20:04), all at a load average of 30–65 on 16 cores. Stability across days and on quiet or CI
  hosts is not shown; the time box's three unattended days were not spent. The decision does not
  depend on it: the gated metrics are exact on any host, and the ungated ones are ungated because they
  were not stable even within twenty minutes.
- **CI numbers are of different commits.** The 40 CI runs built 40 different commits, so their spread
  is an upper bound of the host noise.
- **Processor time is a sampled lower bound** in tick units (10 ms), as `docs/benchmarks/README.md`
  says; it would not gate a program of 10–80 ms even on a quiet host.
- **The fixed runner is unmerged.** If #1347 changes before merging, the peak-memory candidate is to be
  measured again.
