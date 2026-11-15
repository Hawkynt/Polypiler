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
#   llvm         llvm-config, llvm-as and opt from the official release archive of the pinned version,
#                which checks the modules pbc emits.
#   vice         VICE built from the release source of the pinned version, which carries the ROM
#                images, with the headless user interface, and installed under /usr/local: the
#                distribution's package ships no ROMs, and the Flathub build cannot start inside the
#                sandbox. Commands: x64sc, x128, xvic, petcat, c1541. xvfb-run (the distribution's
#                xvfb, with xdpyinfo for the probe) is the display the runner starts the emulators under.
#   pcbasic      the PyPI release in a virtual environment at <prefix>/pcbasic, the folder the caller
#                exports as PCBASIC_HOME.
#
# llvm and the VICE build are kept in <cache>, which the job restores and saves with actions/cache
# under a key of their versions; a filled cache is used as it is.
#
# Usage: install.sh <env file> <prefix> <cache>
set -euo pipefail

env_file=$1
prefix=$2
cache=$3
version() { sed -n "s/^$1=//p" "$env_file"; }

mkdir -p "$prefix/bin" "$cache"

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

# The distribution's packages: dosbox-x, the display of the VICE emulators, what VICE is built with,
# and the venv module of the runner's Python. A Flatpak application cannot start in the runner's
# bubblewrap sandbox: Flatpak's own bubblewrap is refused a nested user namespace there.
sudo apt-get update -qq
sudo apt-get install -y -qq dosbox-x xvfb x11-utils xa65 dos2unix bison flex libpng-dev zlib1g-dev python3-venv > /dev/null
ln -sfn "$(command -v dosbox-x)" "$prefix/bin/dosbox-x"
dpkg-query -W -f='dosbox-x ${Version}\n' dosbox-x

# llvm: three tools of the release archive. The archive is large; only these members are unpacked.
llvm=$(version llvm)
if [ ! -x "$cache/llvm-$llvm/bin/opt" ]; then
  name="LLVM-$llvm-Linux-X64"
  curl -fsSL --retry 3 -o "$prefix/llvm.tar.xz" "https://github.com/llvm/llvm-project/releases/download/llvmorg-$llvm/$name.tar.xz"
  mkdir -p "$cache/llvm-$llvm"
  tar -xJf "$prefix/llvm.tar.xz" -C "$cache/llvm-$llvm" --strip-components 1 "$name/bin/llvm-config" "$name/bin/llvm-as" "$name/bin/opt"
  rm "$prefix/llvm.tar.xz"
fi
for tool in llvm-config llvm-as opt; do
  if ldd "$cache/llvm-$llvm/bin/$tool" 2>/dev/null | grep 'not found'; then
    echo "$tool needs a library the archive's three tools do not bring" >&2
    exit 1
  fi
  ln -sfn "$cache/llvm-$llvm/bin/$tool" "$prefix/bin/$tool"
done

# vice: built once per version into <cache>/vice-<version>, then copied to /usr/local, where the
# emulators look for their ROM images.
vice=$(version vice)
if [ ! -x "$cache/vice-$vice/usr/local/bin/x64sc" ]; then
  build="$prefix/vice-src"
  rm -rf "$build" && mkdir -p "$build"
  curl -fsSL --retry 3 -o "$prefix/vice.tar.gz" "https://sourceforge.net/projects/vice-emu/files/releases/vice-$vice.tar.gz/download"
  tar -xzf "$prefix/vice.tar.gz" -C "$build" --strip-components 1
  rm "$prefix/vice.tar.gz"
  (cd "$build" \
    && ./configure --prefix=/usr/local --enable-headlessui --disable-pdf-docs --disable-html-docs --without-pulse --without-alsa --without-oss --without-libcurl > configure.log \
    && make -j"$(nproc)" > make.log \
    && make install DESTDIR="$cache/vice-$vice" > install.log)
  rm -rf "$build"
fi
sudo cp -a "$cache/vice-$vice/usr/local/." /usr/local/

# pcbasic
python3 -m venv "$prefix/pcbasic"
"$prefix/pcbasic/bin/pip" install --quiet --disable-pip-version-check "pcbasic==$(version pcbasic)"
ln -sfn "$prefix/pcbasic/bin/pcbasic" "$prefix/bin/pcbasic"
