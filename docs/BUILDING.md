# Building Lycoreia

## What you need
- .NET SDK 10, the same as CI (the mod targets `net6.0`).
- A Slime Rancher 2 install with MelonLoader 0.7.x and Starlight, started once so MelonLoader has
  generated `MelonLoader/Il2CppAssemblies`.

The game's interop assemblies, MelonLoader and Starlight are **not** in this repository (they are not
ours to redistribute). The build reads them from `SR2MP/libraries/`, which is git-ignored.

## Local build
```bash
# 1. Fill SR2MP/libraries from your game install (copies real files, no symlinks)
tools/pack-reference-libs.sh "/path/to/Slime Rancher 2" --into SR2MP/libraries

# 2. Build
dotnet build SR2MP/SR2MP.csproj -c Release -o out/release
dotnet build SR2MP/SR2MP.csproj -c Release -p:DeepLog=true -o out/deeplog   # diagnostics build
```
Output: `out/*/SR2MP.dll` → copy into the game's `Mods` folder.

Build switches:
| Switch | Effect |
|---|---|
| `-p:DeepLog=true` | Detailed diagnostics (`UserData/Lycoreia/diagnostics.jsonl`) |
| `-p:TestHarness=true` | Automated-test hooks. Never ship this build to players. |

## Releases (GitHub Actions)

`.github/workflows/release.yml` builds and publishes a release from the Actions tab:
**Actions → Build release → Run workflow**, then fill in:

| Input | Example | Meaning |
|---|---|---|
| `version` | `1.0.0` or `1.0.0-rc.1` | Mod version; also the git tag `v<version>` |
| `release_name` | `Lycoreia 1.0.0 RC 1` | Title of the GitHub release |
| `prerelease` | ✔ | Mark as pre-release |
| `deeplog` | ✔ | Also publish the `-deeplog` diagnostics package |
| `notes` | free text | Prepended to the auto-generated release notes |

The workflow stamps the version into `BuildInfoConstants.cs` (in the build only, not committed),
builds both packages, writes `SHA256SUMS.txt`, creates the tag and publishes the release.

### One-time setup: reference libraries for CI
CI needs the same reference DLLs as a local build. Keep them **private**:

1. Make the archive locally:
   `tools/pack-reference-libs.sh "/path/to/Slime Rancher 2" --zip reference-libs.zip`
2. Upload it somewhere private, e.g. as a release asset of a **private** repository.
3. In this repository: **Settings → Secrets and variables → Actions**, add:
   - `REFERENCE_LIBS_URL` — download URL of the zip. For a private GitHub release asset use the API
     URL: `https://api.github.com/repos/<owner>/<private-repo>/releases/assets/<asset-id>`.
   - `REFERENCE_LIBS_TOKEN` — (optional) token with read access to that private repo.
   - `REFERENCE_LIBS_SHA256` — (optional) expected checksum of the zip.

Re-create the archive whenever the game, MelonLoader or Starlight updates.
Pull requests from forks do not get secrets, so `build.yml` skips the build for them.

### After publishing a stable release
Update `latestModVersion.txt` on `main` to the new version (e.g. `1.0.0`). The mod reads it at startup
to tell players when they are outdated.
