#!/usr/bin/env bash
# Collects the reference assemblies Lycoreia compiles against from a local game install.
# These files belong to the game / MelonLoader / Starlight: keep the result private, never commit it.
#
# Usage:
#   tools/pack-reference-libs.sh "<game dir>" --into SR2MP/libraries   # for a local build
#   tools/pack-reference-libs.sh "<game dir>" --zip reference-libs.zip  # for CI (private upload)
set -euo pipefail

game_dir="${1:-}"
mode="${2:-}"
target="${3:-}"
if [[ -z "$game_dir" || -z "$mode" || -z "$target" ]]; then
  sed -n '2,8p' "$0"; exit 2
fi

sources=(
  "$game_dir/MelonLoader/net6"
  "$game_dir/MelonLoader/Il2CppAssemblies"
)
for dir in "${sources[@]}"; do
  [[ -d "$dir" ]] || { echo "missing: $dir (start the game once with MelonLoader)" >&2; exit 1; }
done
[[ -f "$game_dir/Mods/Starlight.dll" ]] || { echo "missing: $game_dir/Mods/Starlight.dll" >&2; exit 1; }

staging="$(mktemp -d)"
trap 'rm -rf "$staging"' EXIT
for dir in "${sources[@]}"; do
  cp -L "$dir"/*.dll "$staging/"
done
cp -L "$game_dir/Mods/Starlight.dll" "$staging/"
echo "collected $(ls "$staging" | wc -l) assemblies"

case "$mode" in
  --into)
    mkdir -p "$target"
    cp "$staging"/*.dll "$target/"
    echo "copied into $target"
    ;;
  --zip)
    out="$(realpath -m "$target")"
    if command -v zip >/dev/null; then
      (cd "$staging" && zip -q -9 "$out" ./*.dll)
    else
      python3 -I -c 'import sys,zipfile,pathlib
src=pathlib.Path(sys.argv[1])
with zipfile.ZipFile(sys.argv[2],"w",zipfile.ZIP_DEFLATED,compresslevel=9) as z:
    [z.write(f,f.name) for f in sorted(src.glob("*.dll"))]' "$staging" "$out"
    fi
    sha256sum "$out"
    ;;
  *)
    echo "unknown mode: $mode" >&2; exit 2
    ;;
esac
