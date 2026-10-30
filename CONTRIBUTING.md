# Contributing to Lycoreia

Thanks for helping! A few ground rules keep co-op stable:

1. **The host is the authority.** Guests ask, the host decides and broadcasts. New sync code should
   follow this pattern (see existing `Shared/Managers/Network*Manager.cs`).
2. **Never apply a received change so it republishes.** Wrap applying network state in
   `HandlingPacket = true` (restore the previous value afterwards).
3. **Update game state the way the game expects.** Prefer the game's own methods (`Set`, `Register…`,
   `Unlock…`) over replacing objects; replacing an observable or dictionary silently breaks the game's
   listeners.
4. **Wire changes are breaking.** If you add or change a packet, say so in the PR; the release
   version must change.
5. **Diagnostics over guesses.** Add a `Logger.LogDiagnostic(category, event, details)` line to new
   sync paths so bugs can be traced from players' logs.
6. **Use the mod's identity, not literals.** Show the name and version through `BuildInfo.Name` /
   `BuildInfo.Version`, and write files under `ModPaths.DataDirectory` (`UserData/Lycoreia`), never
   `UserData/SR2MP`.
7. **No game files.** Never commit anything from the game, MelonLoader, Starlight or `SR2MP/libraries/`,
   and never commit players' logs or saves.

Build instructions: [docs/BUILDING.md](docs/BUILDING.md).
By contributing you agree that your contribution is licensed under GPLv3.
