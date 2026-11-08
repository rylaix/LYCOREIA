# Contributing to Lycoreia

Thank you for helping make co-op better. Lycoreia is **source-available, not open source**: you may read
the code, report bugs and send pull requests, but you may not copy it, reuse it or publish your own
version. Please read [LICENSE](LICENSE) first; by opening a pull request you agree to its Section 4
(Contributions).

## Reporting bugs
- Use the **Bug report** form in Issues. Co-op bugs almost always need logs from **both** players.
- Reproduce with the `-deeplog` package if you can; logs are in `UserData/Lycoreia/`
  (`latest.log`, `diagnostics.jsonl`).
- Logs can contain IP addresses and player IDs: search them for your public IP and replace it with
  `x.x.x.x` before attaching if you do not want it published.
- Questions about installing or playing belong on [Discord](https://discord.gg/EvD7WhSwqh), not in issues.

## Pull requests
Keep them small and focused, one fix or feature per PR, and link the issue. Ground rules that keep
co-op stable:

1. **The host is the authority.** Guests ask, the host decides and broadcasts. Never let a guest change
   shared state on its own.
2. **Applying a received change must not republish it.** Wrap applying network state in
   `using (Applying.Enter()) { ... }`.
3. **Use the game's own methods** (`Unlock`, `Register…`, `Set…`) instead of replacing objects; replacing
   an observable or dictionary silently breaks the game's listeners.
4. **Wire changes are breaking.** If you add or change a message, say so in the PR: every player must
   update together.
5. **Diagnostics over guesses.** New sync paths record a `Mod.Diagnostics?.Record(category, event, details)`
   line so problems can be traced from players' logs.
6. **Tests.** Pure logic goes into the Unity-free core and gets a test in `src/Lycoreia.Tests`.
   `tools/build.sh` must pass in all three flavours.
7. **Your own work only.** Do not submit code copied from other mods or projects, and never commit game
   files, MelonLoader files, the contents of `lib/`, logs or saves.

Build instructions: [docs/BUILDING.md](docs/BUILDING.md).
