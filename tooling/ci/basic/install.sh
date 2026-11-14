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
#   dosbox-x     from Flathub (com.dosbox_x.DOSBox-X): upstream publishes no Linux binaries, and the
#                distribution's package is years behind the pin.
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

# dosbox-x and vice
sudo apt-get update -qq
sudo apt-get install -y -qq flatpak > /dev/null
sudo flatpak remote-add --system --if-not-exists flathub https://dl.flathub.org/repo/flathub.flatpakrepo
sudo flatpak install --system -y --noninteractive flathub com.dosbox_x.DOSBox-X net.sf.VICE

# The runner starts every step in a fresh directory below /tmp with HOME pointing there; the host's
# /tmp is shared so that the emulator sees the directory it is started in.
wrap() { # $1 = command, $2 = Flatpak application, $3 = command inside it
  cat > "$prefix/bin/$1" <<EOF
#!/bin/sh
exec flatpak run --system --filesystem=host --filesystem=/tmp --command=$3 $2 "\$@"
EOF
  chmod +x "$prefix/bin/$1"
}
wrap dosbox-x com.dosbox_x.DOSBox-X dosbox-x
for command in x64sc x128 xvic petcat c1541; do
  wrap "$command" net.sf.VICE "$command"
done

flatpak info --system com.dosbox_x.DOSBox-X | sed -n 's/^ *\(Version\|Commit\):/dosbox-x \1:/p'
flatpak info --system net.sf.VICE | sed -n 's/^ *\(Version\|Commit\):/vice \1:/p'
