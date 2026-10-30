#!/usr/bin/env bash
# Stamps a release version into SR2MP/BuildInfoConstants.cs.
#   Version      = full version, e.g. 1.0.0-rc.1 (shown by Starlight; peers must match exactly)
#   MelonVersion = numeric part, e.g. 1.0.0      (MelonLoader requires x.y.z)
# Usage: tools/set-version.sh 1.0.0-rc.1
set -euo pipefail

version="${1:-}"
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$ ]]; then
  echo "invalid version '$version' (expected x.y.z or x.y.z-suffix)" >&2
  exit 1
fi
numeric="${version%%-*}"
file="SR2MP/BuildInfoConstants.cs"

sed -i -E \
  -e "s/(internal const string Version = )\"[^\"]*\";/\1\"$version\";/" \
  -e "s/(internal const string MelonVersion = )\"[^\"]*\";/\1\"$numeric\";/" \
  "$file"

grep -q "Version = \"$version\";" "$file" || { echo "Version was not stamped" >&2; exit 1; }
grep -q "MelonVersion = \"$numeric\";" "$file" || { echo "MelonVersion was not stamped" >&2; exit 1; }
echo "stamped $version (MelonVersion $numeric)"
