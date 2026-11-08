# Building Lycoreia

Building is only needed to test a contribution; players should download an
[official release](https://github.com/rylaix/Lycoreia/releases). See [LICENSE](../LICENSE) for what local
builds may be used for.

## What you need
- .NET SDK 10 (the mod targets `net6.0`, the runtime MelonLoader uses).
- Slime Rancher 2 with **MelonLoader 0.7.x**, started once so MelonLoader has generated
  `MelonLoader/Il2CppAssemblies`.

The game's interop assemblies and MelonLoader are **not** in this repository (they are not ours to
redistribute). The build reads them from `lib/`, which is git-ignored.

## Build
```bash
# 1. Fill lib/ from your own game install
tools/pack-reference-libs.sh "/path/to/Slime Rancher 2"

# 2. Build all three flavours and run the tests
tools/build.sh
```

| Flavour | Output | Purpose |
|---|---|---|
| release | `out/release/Lycoreia.dll` | What players run |
| deeplog | `out/deeplog/Lycoreia.dll` | Same, plus detailed diagnostics in `UserData/Lycoreia/diagnostics.jsonl` |
| test | `out/test/Lycoreia.dll` | Automated-test hooks; never give this build to players |

Single flavour: `dotnet build src/Lycoreia.Core/Lycoreia.Core.csproj -p:Flavour=release -o out/release`.

To try a build, copy `Lycoreia.dll` into the game's `Mods` folder on **every** player's machine: all
players must run the identical build.

## Layout
| Path | Contents |
|---|---|
| `src/Lycoreia.Core` | The mod: wire protocol and transport, sessions, snapshots, actor/world/player sync, game adapters and hooks, UI, Discord presence |
| `src/Lycoreia.Owned` | Feature modules compiled into the same DLL (avatars and rigs, chat, pings, inventory, shop/upgrade managers, menus) |
| `src/Lycoreia.Tests` | Self-contained test runner (no test framework needed): `dotnet out/tests-release/Lycoreia.Tests.dll` |
| `tools/` | `build.sh`, `pack-reference-libs.sh` |

## CI
`.github/workflows/build.yml` builds and tests every push and pull request. It needs the private
secret `REFERENCE_LIBS_URL` (a zip made with `tools/pack-reference-libs.sh "<game dir>" --zip libs.zip`,
optionally `REFERENCE_LIBS_TOKEN` and `REFERENCE_LIBS_SHA256`). Pull requests from forks get no secrets,
so their build is skipped and runs after review.
