#!/usr/bin/env bash
# Installs the toolchains of the BASIC/PB layer (tooling/ci/layers/basic.yaml) on a Linux runner, at the
# versions `polypiler-tooling ci env --layer basic` printed into <env file>, and puts one command per
# toolchain into <prefix>/bin, which the caller adds to PATH. The probe of tooling/toolchains.lock then
# decides whether each one is at its pin; nothing here checks a version.
#
#   pb-compiler  built with the .NET SDK from the pinned commit that `polypiler-tooling corpus import`
#                fetched into corpus/upstream/Hawkynt/PB-Compiler@<commit>/, never from a branch.
#                pbc has no version option of its own (`pbc --version` says "no source file",
#                docs/llvm/pb-emitter-audit.md, finding F11), so the command `pbc` is a wrapper that
#                answers --version with the commit it was built from and passes everything else on.
#   dosbox-x     the runner distribution's package: upstream publishes no Linux binaries, and the
#                Flathub build cannot start inside the runner's bubblewrap sandbox.
#   vice         from Flathub (net.sf.VICE), which ships the ROM images; the distribution's package
#                does not. Commands: x64sc, x128, xvic, petcat, c1541.
#
# Usage: install.sh <env file> <prefix>
set -euo pipefail

env_file=$1
prefix=$2
version() { sed -n "s/^$1=//p" "$env_file"; }

mkdir -p "$prefix/bin"

# pb-compiler
pin=$(version pb-compiler)
checkout="corpus/upstream/Hawkynt/PB-Compiler@$pin"
commit=$(git -C "$checkout" rev-parse --verify "$pin^{commit}")
sources="$prefix/pb-compiler-src"
rm -rf "$sources" && mkdir -p "$sources"
git -C "$checkout" archive "$commit" | tar -x -C "$sources"
dotnet build "$sources/pbc/pbc.csproj" -c Release -o "$prefix/pb-compiler" -nologo -v q -clp:ErrorsOnly
dotnet=$(command -v dotnet)
# The wrapper lives beside pbc.dll and <prefix>/bin/pbc links to it: the bubblewrap sandbox hides the
# home directory, the runner's work folder with it, and shows only the folders of the command and of
# its link target.
cat > "$prefix/pb-compiler/pbc.sh" <<EOF
#!/bin/sh
# pbc of Hawkynt/PB-Compiler, built from commit $commit by tooling/ci/basic/install.sh.
if [ "\$#" -eq 1 ] && [ "\$1" = --version ]; then
  echo "pbc (Hawkynt/PB-Compiler $commit)"
  exit 0
fi
exec "$dotnet" "$prefix/pb-compiler/pbc.dll" "\$@"
EOF
chmod +x "$prefix/pb-compiler/pbc.sh"
ln -sfn "$prefix/pb-compiler/pbc.sh" "$prefix/bin/pbc"

# dosbox-x: the distribution's package, a native binary. The runner's bubblewrap sandbox cannot start
# a Flatpak application: Flatpak's own bubblewrap is refused a nested user namespace there.
sudo apt-get update -qq
sudo apt-get install -y -qq dosbox-x flatpak > /dev/null
ln -sfn "$(command -v dosbox-x)" "$prefix/bin/dosbox-x"
dpkg-query -W -f='dosbox-x ${Version}\n' dosbox-x

# vice: Flathub, which ships the ROM images. The probe runs outside the sandbox; running VICE inside
# it needs a native build, for the same reason as DOSBox-X (FMT-CBMBASIC-ORACLE).
sudo flatpak remote-add --system --if-not-exists flathub https://dl.flathub.org/repo/flathub.flatpakrepo
sudo flatpak install --system -y --noninteractive flathub net.sf.VICE
for command in x64sc x128 xvic petcat c1541; do
  cat > "$prefix/bin/$command" <<EOF
#!/bin/sh
exec flatpak run --system --filesystem=host --filesystem=/tmp --command=$command net.sf.VICE "\$@"
EOF
  chmod +x "$prefix/bin/$command"
done
flatpak info --system net.sf.VICE | sed -n 's/^ *\(Version\|Commit\):/vice \1:/p'
