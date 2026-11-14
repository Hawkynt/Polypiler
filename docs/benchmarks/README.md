# Benchmarks

The benchmark harness measures speed, memory and code size (PRD §13.2) and records what it measured.
It never declares a change a win: a number is gated only by a budget an ADR has set
(ADR-PERF-BUDGETS), and only against a baseline from the same machine and toolchains.

## Adding a benchmark

A benchmark is one file, `tests/benchmarks/<id>.bench.yaml`, checked by
[`spec/schemas/bench.schema.json`](../../spec/schemas/bench.schema.json):

```yaml
id: sum-loop                       # names the file and the results
description: Sums 0 to 999 999 in node and prints the sum.
kind: process                      # process | dotnet
toolchain: node                    # a key of tooling/toolchains.lock
sources: [tests/fixtures/bench/sum-loop.js]
build: [...]                       # optional arguments of a build step on the same toolchain
run: [tests/fixtures/bench/sum-loop.js]
stdin: tests/fixtures/bench/input  # optional
expectStdout: "499999500000"       # optional; a run that prints other is not a measurement
variants:                          # optional; without it there is one variant, default
  - { name: opt-none, args: [--opt, none] }
  - { name: opt-speed, args: [--opt, speed], env: { POLYPILER_OPT: speed } }
warmup: 1                          # runs whose samples are discarded (default 1)
runs: 5                            # measured runs (default 5)
sizes:
  source: [tests/fixtures/bench/sum-loop.js]   # summed as sourceBytes
  binary: [out]                                # read from the run's directory after the build
```

Paths are relative to the repository root; `sizes.binary` is relative to the run's directory, and a
directory counts every file below it. In `build` and `run`, `{root}` is the repository root and
`{configuration}` the configuration `polypiler-tooling` was built in; `{configuration}` is replaced in
`sources` too.

**`process`** runs the program `warmup + runs` times through the sandboxed cross-language runner
(`Polypiler.Core.Verification/Running`), each run in a fresh directory, and records each measured run's
wall time, processor time and peak resident memory as one sample. Processor time and peak memory come
from sampling the process tree every 20 ms - on Linux by following the tree's children in `/proc`, so
that a sample costs what the tree holds and not what the machine runs - and, on Linux, from the
kernel's account of the step once it ends. That account covers a program the runner started itself;
inside bubblewrap it covers bubblewrap alone, which does not wait for the program's namespace, so
there both figures are what the samples saw. Both are therefore lower bounds for a program of a few
milliseconds, and a program that ends before the first sample may record 0: the numbers are recorded,
never judged.

**`dotnet`** starts a BenchmarkDotNet program once - [`Polypiler.Benchmarks`](../../Polypiler.Benchmarks)
with `--filter` - and converts its full JSON export: each measured iteration is a sample of the time of
one operation, and the memory diagnoser's bytes per operation are `allocatedBytes`. The program runs in
process (`InProcessNoEmitToolchain`), because the sandbox has no network to restore the project
BenchmarkDotNet would otherwise generate. A new in-process benchmark is a class with a `[Benchmark]`
method in `Polypiler.Benchmarks` and a definition that copies the project's build output, as
[`cli-startup.bench.yaml`](../../tests/benchmarks/cli-startup.bench.yaml) does: the sandbox hides the
caller's home, where a checkout may lie. Build the solution in the configuration `polypiler-tooling`
runs in first.

A run that the runner ends (time, memory or output limit, `PLY0031`-`PLY0033`; a missing toolchain,
`PLY0021`), that exits non-zero (`PLY0079`) or prints other than `expectStdout` (`PLY0051`, exit code 5)
is not a measurement: the variant gets no result file, and an earlier one is removed.

## Running and reading

```bash
dotnet run --project Polypiler.Tooling -c Release -- bench run --id sum-loop   # or --all
dotnet run --project Polypiler.Tooling -c Release -- bench compare --baseline old/ --current artifacts/bench [--budgets tooling/bench/budgets.yaml] [--out compare.md]
```

`bench run` writes `artifacts/bench/<id>/<variant>.json` (schema
[`spec/schemas/bench-result.schema.json`](../../spec/schemas/bench-result.schema.json)) and
`artifacts/bench/summary.md`, one row per benchmark and variant, each with the note `record only`.
A result holds:

| Field | Meaning (PRD §13.2) |
| --- | --- |
| `samples[].wallNs`, `cpuNs` | execution time per measured run, warm-up excluded |
| `samples[].peakRssBytes` | peak memory |
| `stats.<metric>` | `median`, `mad`, `min`, `max` of each sample metric |
| `buildNs` | build time, the median over the measured runs |
| `sourceBytes`, `binaryBytes` | generated source size and binary size |
| `allocatedBytes` | compilation cost in process: bytes allocated per operation (`dotnet`) |
| `allocations` | allocation count, where a measurement provides it; BenchmarkDotNet reports bytes only, so it is `null` today |
| `extra` | further named metrics a caller supplies, for example instruction counts |
| `environment` | operating system, its version, architecture, CPU model, logical cores, toolchain versions and their SHA-256 `fingerprint` |

The statistics are integer nanoseconds and bytes. The median of an even count is the mean of the two
middle samples, rounded half to even; `mad` is the median absolute deviation from the median (Hampel,
JASA 69, 1974). No sample is removed as an outlier: the slow run stays in `max` and widens `mad`.

## Comparing and budgets

`bench compare` pairs results by ID and variant and prints a table of every metric with the baseline,
the current value and the relative change:

- a pair from different environments is not compared: `PLY0072` names the first field that differs;
- with `maxRelativeMad` in the budget file, a pair whose wall time has `mad / median` above it is
  unstable and not compared (`PLY0072`);
- a metric with a budget regresses when `current > baseline * (1 + maxRegressionPercent / 100)`; the
  limit itself passes; a regression is `PLY0073`, exit code 1;
- a metric without a budget is `record only`, however large the change;
- a baseline of 0 is never divided by: 0 to 0 is `0 %`, 0 to more is `new value`;
- a benchmark on one side only is `new` or `removed`, never a regression.

The budget file has this form; `tooling/bench/budgets.yaml` with real numbers is ADR-PERF-BUDGETS' to
write after a baseline was measured on the pinned CI machine, and
[`tests/fixtures/bench/budgets-test.yaml`](../../tests/fixtures/bench/budgets-test.yaml) exists only to
test the comparison:

```yaml
schemaVersion: 1
maxRelativeMad: 0.1            # optional
budgets:
  - { id: sum-loop, variant: default, metric: wallNs, maxRegressionPercent: 10 }
```

Related: [verification and release gates](../quality/VERIFICATION.md) · [documentation index](../README.md).
