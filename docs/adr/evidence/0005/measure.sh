#!/usr/bin/env bash
# Reproduces the measurement of ADR 0005: builds every runner, evaluates the fixed-seed input set
# on each runtime and prints the tables. Usage: measure.sh <work directory>
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
work="${1:?usage: measure.sh <work directory>}"
mkdir -p "$work" && cd "$work"

python3 "$here/gen_inputs.py" > inputs.txt

gcc -O2 -fno-builtin -o run_c "$here/run.c" -lm
gcc -O2 -fno-builtin -DX87 -o run_x87 "$here/run.c" -lm
gcc -O2 -o reference "$here/reference.c" -lmpfr -lgmp -lm
rustc -O -o run_rs "$here/run.rs"
mkdir -p go && cp "$here/run.go" go/ && (cd go && { [ -f go.mod ] || go mod init measure >/dev/null 2>&1; } && go build -o ../run_go .)
javac -d java "$here/Run.java"
# the project file is written here: a committed one would count as a module of the solution
mkdir -p dotnet && cp "$here/run.cs" dotnet/Run.cs
cat > dotnet/Run.csproj <<'PROJECT'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
</Project>
PROJECT
dotnet build dotnet/Run.csproj -c Release -o dotnet-out --nologo -v quiet >/dev/null

./reference < inputs.txt > out.reference.txt
./run_c < inputs.txt > out.gcc.txt
./run_x87 < inputs.txt > out.x87.txt
./run_rs < inputs.txt > out.rust.txt
./run_go < inputs.txt > out.go.txt
java -cp java Run < inputs.txt > out.jdk.txt
java -cp java Run strict < inputs.txt > out.jdk-strict.txt
dotnet dotnet-out/Run.dll < inputs.txt > out.dotnet-sdk.txt
node "$here/run.mjs" < inputs.txt > out.node.txt
python3 "$here/run.py" < inputs.txt > out.python.txt
perl "$here/run.pl" < inputs.txt > out.perl.txt

{
  echo "host: $(uname -srm)"
  echo "cpu: $(grep -m1 'model name' /proc/cpuinfo | cut -d: -f2- | sed 's/^ //')"
  echo "glibc: $(ldd --version | head -1)"
  echo "gcc: $(gcc --version | head -1)"
  echo "rust: $(rustc --version)"
  echo "go: $(go version)"
  echo "jdk: $(java -version 2>&1 | head -1)"
  echo "dotnet-sdk: $(dotnet --version)"
  echo "node: $(node --version)"
  echo "python: $(python3 --version)"
  echo "perl: $(perl -e 'print $^V')"
  echo "mpfr: $(printf '#include <mpfr.h>\nMPFR_VERSION_STRING' | gcc -E -P - | tail -1 | tr -d '"')"
  echo "inputs.txt sha256: $(sha256sum inputs.txt | cut -d' ' -f1)"
} > versions.txt

python3 "$here/compare.py" . > report.md
python3 "$here/falsepass.py" > falsepass.md
python3 "$here/lemma.py" > lemma.txt
gcc -O1 -o cost "$here/cost.c" -lm && ./cost > cost.txt
sha256sum out.*.txt > outputs.sha256
cat versions.txt report.md falsepass.md cost.txt
