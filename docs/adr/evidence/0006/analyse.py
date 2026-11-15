#!/usr/bin/env python3
"""The statistics and the replay of ADR 0006, from the series `polypiler-tooling bench run` wrote.

    analyse.py <campaign dir> <runner> [<runner> ...] [--csv <dir>] [--split <series>]

A campaign directory holds one directory per runner build (`fixed`, `old`), each with one directory
per series (`s01`, `s02`, ...); a series holds `load.txt` (the 1, 5 and 15 minute load averages of
/proc/loadavg at its start, before each benchmark and at its end) and the result files
`<id>/<id>/<variant>.json`. With --csv the samples and the series are written as
`samples-<runner>.csv` and `series-<runner>.csv` into the directory given, which is how the raw
numbers under docs/benchmarks/baseline-p0/ were made; the report goes to standard output.

Statistics per metric over the pooled samples: mean, median, relative standard deviation, and the
95th and 99th percentile of the absolute deviation from the median relative to the median. Per series
the gate compares medians, so the replay compares the median of every series with the median of every
other (all ordered pairs): a pair of identical code whose ratio exceeds the threshold is a false
alarm; the same pair with the current samples multiplied by 1.05, 1.10 and 1.25 is an injected
slowdown, detected when the ratio exceeds the threshold. Percentiles are nearest-rank. With --split
the series from the one named on form a second measurement window, and the median of the series
medians of each window is compared, which is how the stability between windows is judged.
"""
import csv
import json
import math
import os
import statistics
import sys

METRICS = ("wallNs", "cpuNs", "peakRssBytes")
THRESHOLDS = (5, 10, 15, 20, 25, 50, 100)
SLOWDOWNS = (5, 10, 25)
MISSED_BELOW = 16 * 1024 * 1024


def percentile(values, p):
    ordered = sorted(values)
    return ordered[max(0, math.ceil(p / 100 * len(ordered)) - 1)]


def load_series(directory):
    """{series: {"load": {label: (l1, l5, l15)}, "results": {(id, variant): result}}}"""
    series = {}
    for name in sorted(os.listdir(directory)):
        path = os.path.join(directory, name)
        if not os.path.isdir(path):
            continue
        loads = {}
        with open(os.path.join(path, "load.txt")) as text:
            for line in text:
                fields = line.split()
                loads[fields[0]] = tuple(float(value) for value in fields[2:5])
        results = {}
        for root, _, files in os.walk(path):
            for file in files:
                if file.endswith(".json"):
                    with open(os.path.join(root, file)) as handle:
                        result = json.load(handle)
                    results[(result["id"], result["variant"])] = result
        series[name] = {"load": loads, "results": results}
    return series


def write_csv(runner, series, out):
    with open(os.path.join(out, f"samples-{runner}.csv"), "w", newline="") as handle:
        writer = csv.writer(handle, lineterminator="\n")
        writer.writerow(("series", "id", "variant", "run", *METRICS))
        for name, data in series.items():
            for (bench, variant), result in sorted(data["results"].items()):
                for run, sample in enumerate(result["samples"], 1):
                    writer.writerow((name, bench, variant, run, *(sample.get(metric) for metric in METRICS)))
    with open(os.path.join(out, f"series-{runner}.csv"), "w", newline="") as handle:
        writer = csv.writer(handle, lineterminator="\n")
        writer.writerow(("series", "id", "variant", "load1AtStart", "load5AtStart", "commit", "sourceBytes", *(f"{metric}Median" for metric in METRICS), "wallNsMad"))
        for name, data in series.items():
            for (bench, variant), result in sorted(data["results"].items()):
                load = data["load"].get(bench, data["load"]["start"])
                writer.writerow((name, bench, variant, load[0], load[1], result["commit"], result["sourceBytes"],
                                 *(result["stats"][metric]["median"] for metric in METRICS), result["stats"]["wallNs"]["mad"]))


def pearson(xs, ys):
    if len(xs) < 3 or statistics.pstdev(xs) == 0 or statistics.pstdev(ys) == 0:
        return float("nan")
    return statistics.correlation(xs, ys)


def windows(series, split):
    print(f"Stability between windows: series before {split} against series from {split} on, median of the series medians.")
    print()
    print("| id | variant | metric | first window | second window | change % |")
    print("| --- | --- | --- | ---: | ---: | ---: |")
    first = [data for name, data in series.items() if name < split]
    second = [data for name, data in series.items() if name >= split]
    for key in sorted({key for data in series.values() for key in data["results"]}):
        for metric in METRICS:
            a = statistics.median(data["results"][key]["stats"][metric]["median"] for data in first if key in data["results"])
            b = statistics.median(data["results"][key]["stats"][metric]["median"] for data in second if key in data["results"])
            print(f"| {key[0]} | {key[1]} | {metric} | {a:.0f} | {b:.0f} | {(b - a) / a * 100:+.1f} |")
    print()


def report(runner, series):
    print(f"## runner {runner}: {len(series)} series")
    starts = [data["load"]["start"][0] for data in series.values()]
    print(f"load average (1 min) at series start: min {min(starts):.2f}, median {statistics.median(starts):.2f}, max {max(starts):.2f}")
    keys = sorted({key for data in series.values() for key in data["results"]})
    sizes = {key: {data["results"][key]["sourceBytes"] for data in series.values() if key in data["results"]} for key in keys}
    print("sourceBytes identical in every series: " + ", ".join(f"{b}/{v} {sorted(s)}" for (b, v), s in sizes.items()))
    samples = [sample for data in series.values() for result in data["results"].values() for sample in result["samples"]]
    missed = sum(sample["peakRssBytes"] < MISSED_BELOW for sample in samples)
    print(f"samples whose peak memory is below {MISSED_BELOW} bytes, less than node itself needs (the sampler missed the program): {missed} of {len(samples)}")
    print()
    print("| id | variant | metric | n | mean | median | RSD % | p95 dev % | p99 dev % | series medians min..max | RSD of series medians % | r(load, median) |")
    print("| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | --- | ---: | ---: |")
    replay = {}
    for key in keys:
        present = [data for data in series.values() if key in data["results"]]
        for metric in METRICS:
            pooled = [sample[metric] for data in present for sample in data["results"][key]["samples"]]
            median = statistics.median(pooled)
            deviations = [abs(value - median) / median * 100 for value in pooled]
            medians = [data["results"][key]["stats"][metric]["median"] for data in present]
            loads = [data["load"].get(key[0], data["load"]["start"])[0] for data in present]
            rsd_medians = statistics.pstdev(medians) / statistics.mean(medians) * 100
            print(f"| {key[0]} | {key[1]} | {metric} | {len(pooled)} | {statistics.mean(pooled):.0f} | {median:.0f} | "
                  f"{statistics.pstdev(pooled) / statistics.mean(pooled) * 100:.1f} | {percentile(deviations, 95):.1f} | {percentile(deviations, 99):.1f} | "
                  f"{min(medians)}..{max(medians)} | {rsd_medians:.1f} | {pearson(loads, medians):.2f} |")
            ratios = [after / before for i, before in enumerate(medians) for j, after in enumerate(medians) if i != j]
            replay.setdefault(metric, []).extend(ratios)
    print()
    print("Replay over all ordered pairs of series (baseline, current) of every benchmark and variant:")
    print()
    print("| metric | pairs | largest ratio | threshold % | false alarms % | " + " | ".join(f"+{s} % detected %" for s in SLOWDOWNS) + " |")
    print("| --- | ---: | ---: | ---: | ---: | " + " | ".join("---:" for _ in SLOWDOWNS) + " |")
    for metric, ratios in replay.items():
        for threshold in THRESHOLDS:
            limit = 1 + threshold / 100
            false_alarms = sum(ratio > limit for ratio in ratios) / len(ratios) * 100
            detected = [sum(ratio * (1 + s / 100) > limit for ratio in ratios) / len(ratios) * 100 for s in SLOWDOWNS]
            print(f"| {metric} | {len(ratios)} | {max(ratios):.3f} | {threshold} | {false_alarms:.1f} | " + " | ".join(f"{d:.1f}" for d in detected) + " |")
    print()


def main(args):
    out = split = None
    if "--split" in args:
        index = args.index("--split")
        split = args[index + 1]
        del args[index:index + 2]
    if "--csv" in args:
        index = args.index("--csv")
        out = args[index + 1]
        del args[index:index + 2]
    campaign, runners = args[0], args[1:]
    for runner in runners:
        series = load_series(os.path.join(campaign, runner))
        if out:
            os.makedirs(out, exist_ok=True)
            write_csv(runner, series, out)
        report(runner, series)
        if split and any(name >= split for name in series):
            windows(series, split)


if __name__ == "__main__":
    main(sys.argv[1:])
