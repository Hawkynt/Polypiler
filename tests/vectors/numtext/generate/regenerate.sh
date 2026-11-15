#!/bin/sh
# Regenerates tests/vectors/numtext/shortest.txt with node and the .NET SDK, and records their versions.
set -eu
here=$(cd "$(dirname "$0")" && pwd)
target="$here/../shortest.txt"
{
  echo "# Shortest round-trip digits: xorshift64 from the seed 0x2026, 20000 f64 and 10000 f32 bit patterns."
  echo "# f64 <bits> <.NET ToString(InvariantCulture)> <ECMA-262 String(x)> | f32 <bits> <.NET ToString(InvariantCulture)>"
  # dotnet run writes its build messages to standard output as well; only the vectors are kept.
  node "$here/ecma.mjs" 20000 10000 | dotnet run "$here/Generate.cs" | grep -E "^f(32|64) "
} > "$target"
{
  echo "node $(node --version)"
  echo "dotnet $(dotnet --version)"
} > "$here/versions.txt"
