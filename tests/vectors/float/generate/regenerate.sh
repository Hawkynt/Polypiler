#!/usr/bin/env bash
# Regenerates the reference vectors of tests/vectors/float. Needs gcc, GNU MPFR and GMP, and a
# checkout of CORE-MATH for its worst-case files: regenerate.sh <core-math checkout> <work directory>
# The vectors are deterministic: the same MPFR gives the same files, which versions.txt records.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
core="${1:?usage: regenerate.sh <core-math checkout> <work directory>}"
work="${2:?usage: regenerate.sh <core-math checkout> <work directory>}"
mkdir -p "$work"
gcc -O2 -Wall -Wno-missing-field-initializers -o "$work/vectors" "$here/vectors.c" -lmpfr -lgmp -lm
"$work/vectors" "$core/src" "$here/.."
(cd "$here/.." && sha256sum ./*.txt > "$here/vectors.sha256")
