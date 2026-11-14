#!/usr/bin/env python3
"""Restore, build and test durations of the CI test jobs on the GitHub-hosted runners, per host class.

    gh api repos/Hawkynt/Polypiler/actions/runs/<run>/jobs > <dir>/<run>.json   # once per run
    ci_durations.py <dir> [--csv <file>]

Reads the jobs of each run (the GitHub REST API's "list jobs for a workflow run"), keeps the jobs
`ci / test ubuntu-latest` and `ci / test windows-latest` and the duration of their steps Restore,
Build and Test in seconds; the API gives whole seconds. Every run built a different commit, so the
spread is an upper bound of the host noise, not the noise alone. With --csv the durations are written
as one row per run, job and step; the report goes to standard output.
"""
import csv
import json
import os
import statistics
import sys
from datetime import datetime

JOBS = ("ci / test ubuntu-latest", "ci / test windows-latest")
STEPS = ("Restore", "Build", "Test")


def seconds(step):
    start = datetime.fromisoformat(step["started_at"].replace("Z", "+00:00"))
    end = datetime.fromisoformat(step["completed_at"].replace("Z", "+00:00"))
    return (end - start).total_seconds()


def main(args):
    out = None
    if "--csv" in args:
        index = args.index("--csv")
        out = args[index + 1]
        del args[index:index + 2]
    rows = []
    for file in sorted(os.listdir(args[0])):
        with open(os.path.join(args[0], file)) as handle:
            jobs = json.load(handle)["jobs"]
        for job in jobs:
            if job["name"] not in JOBS or job["conclusion"] != "success":
                continue
            for step in job["steps"]:
                if step["name"] in STEPS and step.get("completed_at"):
                    rows.append((job["run_id"], job["head_sha"], job["started_at"], job["name"], step["name"], seconds(step)))
    if out:
        with open(out, "w", newline="") as handle:
            writer = csv.writer(handle, lineterminator="\n")
            writer.writerow(("run", "commit", "jobStartedAt", "job", "step", "seconds"))
            writer.writerows(sorted(rows))
    print("| job | step | runs | min s | median s | max s | RSD % | max / median |")
    print("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |")
    for job in JOBS:
        for name in STEPS:
            values = [row[5] for row in rows if row[3] == job and row[4] == name]
            if not values:
                continue
            median = statistics.median(values)
            print(f"| {job} | {name} | {len(values)} | {min(values):.0f} | {median:.0f} | {max(values):.0f} | "
                  f"{statistics.pstdev(values) / statistics.mean(values) * 100:.1f} | {max(values) / median:.2f} |")


if __name__ == "__main__":
    main(sys.argv[1:])
