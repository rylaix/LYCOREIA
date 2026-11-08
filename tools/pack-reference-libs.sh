#!/usr/bin/env bash
# Copies the reference assemblies Lycoreia compiles against from your own game install into lib/.
# These files belong to the game and MelonLoader: never commit or share them.
#
# Usage:
#   tools/pack-reference-libs.sh "<game dir>"                     # fills lib/ for a local build
#   tools/pack-reference-libs.sh "<game dir>" --zip libs.zip      # private archive for CI
set -euo pipefail
game_dir="${1:-}"; mode="${2:-}"; target="${3:-}"
[[ -n "$game_dir" ]] || { sed -n '2,7p' "$0"; exit 2; }
R="$(cd "$(dirname "$0")/.." && pwd)"
sources=("$game_dir/MelonLoader/net6" "$game_dir/MelonLoader/Il2CppAssemblies")
for dir in "${sources[@]}"; do [[ -d "$dir" ]] || { echo "missing: $dir (start the game once with MelonLoader)" >&2; exit 1; }; done
if [[ "$mode" == "--zip" ]]; then
  staging="$(mktemp -d)"; trap 'rm -rf "$staging"' EXIT
  for dir in "${sources[@]}"; do cp -L "$dir"/*.dll "$staging/"; done
  out="$(realpath -m "$target")"; (cd "$staging" && python3 -m zipfile -c "$out" *.dll)
  echo "wrote $target (keep it private)"
else
  mkdir -p "$R/lib"
  for dir in "${sources[@]}"; do cp -L "$dir"/*.dll "$R/lib/"; done
  echo "lib/: $(ls "$R/lib"/*.dll | wc -l) assemblies"
fi
