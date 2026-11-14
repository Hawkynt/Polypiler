#!/bin/bash
# Reproduces outputs.txt: the licence history of Cipher/LICENSE and LICENSE in Hawkynt/Hawkynt.github.io.
#   bash docs/adr/evidence/0030/run.sh <checkout of Hawkynt/Hawkynt.github.io> > docs/adr/evidence/0030/outputs.txt
# The checkout must contain b3a8a8e92cbca83475986391c3cac56f0b185d29; any later HEAD gives the same output.
set -e
export LC_ALL=C
cd "$1"
fix=b3a8a8e92cbca83475986391c3cac56f0b185d29

show() {
  echo "\$ git show $1 | head -2; sha256"
  git show "$1" | head -2
  git show "$1" | sha256sum | cut -d' ' -f1
  echo
}

echo '$ git log --format="%H %ad %s" --date=short' "$fix -- Cipher/LICENSE"
git log --format='%H %ad %s' --date=short "$fix" -- Cipher/LICENSE
echo
echo '$ git log --format="%H %ad %s" --date=short' "$fix -- LICENSE"
git log --format='%H %ad %s' --date=short "$fix" -- LICENSE
echo
echo "\$ git log --reverse --format=\"%H %ad\" --date=short $fix -- Cipher | head -1"
git log --reverse --format='%H %ad' --date=short "$fix" -- Cipher | head -1
echo
echo "\$ git rev-parse $fix^"
git rev-parse "$fix^"
echo

show 8b4c68ee03e8dbd24a196d08602422e392715575:LICENSE
show 2f7d2c0e961264f61174ae0502cee3cba54c0b43:Cipher/LICENSE
show ee4b6d881e8c554b83138c40006f8c5fe441d1bf:LICENSE
show ee4b6d881e8c554b83138c40006f8c5fe441d1bf:Cipher/LICENSE
show "$fix^:Cipher/LICENSE"
show "$fix:LICENSE"
show "$fix:Cipher/LICENSE"

echo "\$ git log -1 --format=\"%H%n%an%n%ad%n%n%B\" $fix"
git log -1 --format='%H%n%an%n%ad%n%n%B' "$fix"
