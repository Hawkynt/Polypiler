#!/usr/bin/env bash
# Installs the LibreOffice release tooling/toolchains.lock pins (key libreoffice) from the Debian
# packages of The Document Foundation's archive, and puts `soffice` on the PATH. Usage: install.sh <version>
set -euo pipefail
version="$1"
archive="LibreOffice_${version}_Linux_x86-64_deb.tar.gz"
url="https://downloadarchive.documentfoundation.org/libreoffice/old/${version}/deb/x86_64/${archive}"
work="$(mktemp -d)"
curl --fail --location --retry 3 --silent --show-error --output "$work/$archive" "$url"
tar -xzf "$work/$archive" -C "$work"
sudo dpkg -i "$work"/LibreOffice_*_Linux_x86-64_deb/DEBS/*.deb > /dev/null
program="$(ls -d /opt/libreoffice*/program | head -n 1)"
sudo ln -sf "$program/soffice" /usr/local/bin/soffice
soffice --version
