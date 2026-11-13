"""Start-up time of each prototype: median, minimum and 90th percentile of interleaved runs.

usage: measure.py <directory holding single/<name>/polypiler and shared/<name>/polypiler.dll>
"""
import statistics
import subprocess
import sys
import time

BASE = sys.argv[1]
RUNS = 60
WARMUP = 5
TRANSLATE = "translate a.cs --from csharp --to javascript --export A,B --opt speed --verify --output out".split()

variants = {}
for name in ("current", "hand-written", "system-commandline", "spectre"):
  variants[name + ", single file"] = [f"{BASE}/single/{name}/polypiler"]
  variants[name + ", framework-dependent"] = ["dotnet", f"{BASE}/shared/{name}/polypiler.dll"]


def run(command):
  start = time.perf_counter()
  subprocess.run(command, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
  return (time.perf_counter() - start) * 1000


for label, arguments in (("--version", ["--version"]), ("translate", TRANSLATE)):
  samples = {name: [] for name in variants}
  for command in variants.values():
    for _ in range(WARMUP):
      run(command + arguments)
  for _ in range(RUNS):
    for name, command in variants.items():
      samples[name].append(run(command + arguments))
  print(f"{label}: {RUNS} interleaved runs each after {WARMUP} warm-up runs, milliseconds")
  for name, values in samples.items():
    values.sort()
    print(f"  {name:40s} median {statistics.median(values):6.1f}  min {values[0]:6.1f}  p90 {values[int(RUNS * 0.9)]:6.1f}")
