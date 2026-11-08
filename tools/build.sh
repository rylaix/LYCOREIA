#!/usr/bin/env bash
# Builds Lycoreia in the release, deeplog and test flavours and runs the test suite for each.
# Needs the .NET SDK 10 and the reference assemblies in lib/ (see docs/BUILDING.md).
set -euo pipefail
R="$(cd "$(dirname "$0")/.." && pwd)"
[[ -d "$R/lib" ]] || { echo "lib/ is missing: run tools/pack-reference-libs.sh first" >&2; exit 1; }
for f in release deeplog test; do
  dotnet build "$R/src/Lycoreia.Core/Lycoreia.Core.csproj" -p:Flavour=$f -o "$R/out/$f" -nologo -v q
  dotnet build "$R/src/Lycoreia.Tests/Lycoreia.Tests.csproj" -p:Flavour=$f -o "$R/out/tests-$f" -nologo -v q
  dotnet "$R/out/tests-$f/Lycoreia.Tests.dll" | tail -1
done
echo "Mod: $R/out/release/Lycoreia.dll (diagnostics build: out/deeplog)"
