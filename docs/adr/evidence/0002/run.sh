#!/bin/bash
# Reproduces outputs.txt: runs every probe of ADR 0002 on the installed toolchains.
#   bash docs/adr/evidence/0002/run.sh > docs/adr/evidence/0002/outputs.txt
# KOTLINC and PBC name the Kotlin compiler and the pbc executable of Hawkynt/PB-Compiler
# when they are not on the PATH.
export LC_ALL=C
here="$(cd "$(dirname "$0")" && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
KOTLINC="${KOTLINC:-kotlinc}"
PBC="${PBC:-pbc}"
ubCases="1 14 2 16 3 4 5 15 6 7 8 9 10 11 12 13"

echo "### C# - dotnet-sdk $(dotnet --version) - dotnet run probe.cs"
mkdir "$tmp/cs" && cp "$here/probe.cs" "$tmp/cs/" && (cd "$tmp/cs" && dotnet run probe.cs 2>&1)

echo "### JavaScript - node $(node --version) - node probe.js"
node "$here/probe.js"

for cc in gcc clang; do
  echo "### C - $($cc --version | head -1) - $cc -O0 probe.c -lm; ./a.out <case>"
  $cc -O0 -o "$tmp/c_$cc" "$here/probe.c" -lm && "$tmp/c_$cc" 0
  {
    for i in $ubCases; do
      "$tmp/c_$cc" $i > "$tmp/o.txt" 2>&1; s=$?
      if [ $s -gt 128 ]; then echo "case $i: killed by signal $((s - 128))"; else tr -d '\n' < "$tmp/o.txt"; echo; fi
    done
  } 2>/dev/null
  echo "### C - $cc -O0 -fsanitize=undefined,float-cast-overflow probe.c -lm; ./a.out <case>"
  $cc -O0 -fsanitize=undefined,float-cast-overflow -o "$tmp/c_${cc}_ub" "$here/probe.c" -lm
  for i in $ubCases; do
    echo "case $i: $("$tmp/c_${cc}_ub" $i 2>&1 | grep -o 'runtime error:.*' | head -1)"
  done
done

echo "### Rust - $(rustc --version | cut -d' ' -f1-2) - rustc --edition 2021 probe.rs (debug)"
rustc --edition 2021 -o "$tmp/rs_debug" "$here/probe.rs" && "$tmp/rs_debug"
echo "### Rust - rustc --edition 2021 -O probe.rs (release)"
rustc --edition 2021 -O -o "$tmp/rs_release" "$here/probe.rs" && "$tmp/rs_release"

echo "### Kotlin - $("$KOTLINC" -version 2>&1 | tail -1 | sed 's/^info: //') - kotlinc probe.kt -include-runtime -d probe.jar; java -jar probe.jar"
"$KOTLINC" "$here/probe.kt" -include-runtime -d "$tmp/probe.jar" 2> /dev/null && java -jar "$tmp/probe.jar"

echo "### Java - $(java -version 2>&1 | head -1) - javac Probe.java; java Probe"
javac -d "$tmp" "$here/Probe.java" && java -cp "$tmp" Probe

echo "### Go - $(go version | cut -d' ' -f3) - go run probe.go"
go run "$here/probe.go"

echo "### Python - $(python3 --version) - python3 probe.py"
python3 "$here/probe.py"

echo "### Perl - $(perl -e 'print $^V') - perl probe.pl"
perl "$here/probe.pl"

echo "### PowerBASIC - pb-compiler (Hawkynt/PB-Compiler), x64 back end - pbc [--dialect pb36] --platform x64 -O P.out P.BAS; ./P.out"
pb() {
  local label="$1"; shift
  local args=()
  while [ "$1" != "--" ]; do args+=("$1"); shift; done
  shift
  printf '%s\n' "$@" > "$tmp/P.BAS"
  rm -f "$tmp/P.out"
  if ! "$PBC" "${args[@]}" --platform x64 -O "$tmp/P.out" "$tmp/P.BAS" > "$tmp/pbc.log" 2>&1; then
    echo "$label => does not compile"; return
  fi
  local out; out=$(cd "$tmp" && timeout 10 ./P.out 2>&1); local s=$?
  echo "$label => $(echo "$out" | tr '\n' ' ')[exit $s]"
}
pb 'INTEGER 32767 + 1' -- 'DIM a AS INTEGER, b AS INTEGER' 'a = 32767: b = 1' 'a = a + b' 'PRINT a'
pb 'LONG 2147483647 + 1' -- 'DIM a AS LONG, b AS LONG' 'a = 2147483647: b = 1' 'a = a + b' 'PRINT a'
pb 'LONG -2147483647 - 2' -- 'DIM a AS LONG, b AS LONG' 'a = -2147483647: b = 2' 'a = a - b' 'PRINT a'
pb 'LONG 65536 * 65536' -- 'DIM a AS LONG, b AS LONG' 'a = 65536: b = 65536' 'a = a * b' 'PRINT a'
pb 'SHIFT LEFT 1, 32' -- 'DIM a AS LONG, n AS INTEGER' 'a = 1: n = 32' 'SHIFT LEFT a, n' 'PRINT a'
pb 'SHIFT LEFT 1, 33' -- 'DIM a AS LONG, n AS INTEGER' 'a = 1: n = 33' 'SHIFT LEFT a, n' 'PRINT a'
pb 'SHIFT RIGHT -8, 33' -- 'DIM a AS LONG, n AS INTEGER' 'a = -8: n = 33' 'SHIFT RIGHT a, n' 'PRINT a'
pb 'SHIFT LEFT 1, -1' -- 'DIM a AS LONG, n AS INTEGER' 'a = 1: n = -1' 'SHIFT LEFT a, n' 'PRINT a'
pb 'pb36: 1 << 33; 1 << 32; -8 >> 33; -8 >>> 33' --dialect pb36 -- 'DIM a AS LONG, n AS INTEGER, m AS LONG' 'a = 1: n = 33: m = -8' 'PRINT a << n; a << 32; m >> n; m >>> n'
pb 'pb36: fixture 08-shift-rotate-bitwise.bas' --dialect pb36 -- 'DIM n AS INTEGER' 'n = 1' 'PRINT n << 3; n | 4' 'PRINT &H8000 >> 4; &H8000 >>> 4' 'PRINT &H1 <<> 1; &H1 <>> 1'
pb '7 \ 0' -- 'DIM a AS LONG, b AS LONG' 'a = 7: b = 0' 'PRINT a \ b'
pb '7 MOD 0' -- 'DIM a AS LONG, b AS LONG' 'a = 7: b = 0' 'PRINT a MOD b'
pb 'MIN \ -1' -- 'DIM a AS LONG, b AS LONG' 'a = -2147483647 - 1: b = -1' 'PRINT a \ b'
pb 'MIN MOD -1' -- 'DIM a AS LONG, b AS LONG' 'a = -2147483647 - 1: b = -1' 'PRINT a MOD b'
pb '-7 \ 3; -7 MOD 3; 7 MOD -3' -- 'DIM a AS LONG, b AS LONG' 'a = -7: b = 3' 'PRINT a \ b; a MOD b; 7 MOD -b'
pb 'LONG = 2.5; 3.5; -2.5; FIX(-2.7); INT(-2.7)' -- 'DIM d AS DOUBLE, l AS LONG' 'd = 2.5: l = d: PRINT l;' 'd = 3.5: l = d: PRINT l;' 'd = -2.5: l = d: PRINT l;' 'd = -2.7' 'PRINT FIX(d); INT(d)'
pb 'LONG = 3E9' -- 'DIM d AS DOUBLE, l AS LONG' 'd = 3E9: l = d: PRINT l'
pb 'INTEGER = 40000' -- 'DIM d AS DOUBLE, l AS INTEGER' 'd = 40000: l = d: PRINT l'
