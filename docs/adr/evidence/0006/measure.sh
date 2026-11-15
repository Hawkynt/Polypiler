#!/bin/bash
# The measurement series of ADR 0006, run from the repository root:
#
#   measure.sh cipher <polypiler-tooling.dll> <out dir> <first> <last>   # series <first>..<last> of the Cipher baselines
#   measure.sh compile <polypiler-tooling.dll> <out dir>                # clean and no-op builds, fast tier, cli-startup
#
# The DLL is a Release build of Polypiler.Tooling; the "fixed" series used one built from the branch
# 1347-flaky-under-load, the "old" ones one built from main. Every series records /proc/loadavg at its
# start, before each benchmark and at its end in load.txt; analyse.py reads the result.
set -u
mode=$1 dll=$2 out=$3
loadavg() { cat /proc/loadavg; }
case $mode in
  cipher)
    for i in $(seq "$4" "$5"); do
      dir=$out/s$(printf %02d "$i")
      mkdir -p "$dir"
      echo "start $(date -Is) $(loadavg)" > "$dir/load.txt"
      for id in cipher-adler32 cipher-fletcher32 cipher-crc32; do
        echo "$id $(date -Is) $(loadavg)" >> "$dir/load.txt"
        dotnet "$dll" bench run --id "$id" --out "$dir/$id" >> "$dir/log.txt" 2>&1
      done
      echo "end $(date -Is) $(loadavg)" >> "$dir/load.txt"
    done ;;
  compile)
    mkdir -p "$out"
    dotnet build Polypiler.slnx -c Release -v q > "$out/prebuild.log" 2>&1
    echo "step,run,startedAt,load1,load5,wallMs,exit" > "$out/compile.csv"
    step() {
      local name=$1 run=$2; shift 2
      local load at t0 t1 rc
      load=$(cut -d' ' -f1,2 /proc/loadavg | tr ' ' ,) at=$(date -Is) t0=$(date +%s%N)
      "$@" > "$out/$name-$run.log" 2>&1; rc=$?
      t1=$(date +%s%N)
      echo "$name,$run,$at,$load,$(( (t1 - t0) / 1000000 )),$rc" >> "$out/compile.csv"
    }
    for i in 1 2 3 4 5; do
      step clean-build "$i" dotnet build Polypiler.slnx -c Release --no-restore --no-incremental -v q
      step noop-build "$i" dotnet build Polypiler.slnx -c Release --no-restore -v q
    done
    for i in 1 2 3; do
      step fast-tests "$i" dotnet test Polypiler.Tests/Polypiler.Tests.csproj -c Release --no-build \
        --filter "TestCategory!=Slow&TestCategory!=EndToEnd&TestCategory!=OsIntegration&TestCategory!=ExternalInterop&TestCategory!=PolyglotInterop&TestCategory!=Performance"
    done
    for i in $(seq 1 10); do
      dir=$out/cli-startup/s$(printf %02d "$i")
      mkdir -p "$dir"
      echo "start $(date -Is) $(loadavg)" > "$dir/load.txt"
      dotnet "$dll" bench run --id cli-startup --out "$dir/cli-startup" > "$dir/log.txt" 2>&1
      echo "end $(date -Is) $(loadavg)" >> "$dir/load.txt"
    done ;;
esac
