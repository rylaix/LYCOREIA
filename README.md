# Lycoreia

**Host-authoritative multiplayer co-op mod for Slime Rancher 2.**
Developed by **[Rylaix](https://github.com/rylaix)** (core developer) with
**[Kum1te](https://github.com/kum1te)** (core QA). Free software under GPLv3.

[Download](https://github.com/rylaix/Lycoreia/releases) ·
[Nexus Mods](https://www.nexusmods.com/slimerancher2/mods/200) ·
[Discord](https://discord.gg/EvD7WhSwqh) ·
[Report a bug](https://github.com/rylaix/Lycoreia/issues/new/choose) ·
[Changelog](CHANGELOG.md)

> Lycoreia was the town on Mount Parnassus founded by the survivors of Deucalion's flood.
> This project is the ranch rebuilt on higher ground.

---

## What it does

Two or more players share one Slime Rancher 2 world. The **host** owns the save and is the single
source of truth; guests join over LAN or the internet and see the same ranch, slimes, items and
progress.

### What Lycoreia adds over Ranching Together 0.3.9

- **Host-authoritative world**: the host grants ownership of slimes and items (two-phase commit with
  epochs and an ownership arbiter), so two players grabbing the same slime no longer split the world.
- **Reliable netcode**: no more lost packets, sequence deadlocks or reconnect storms; tested under
  simulated packet loss, jitter and bad Wi-Fi.
- **Full-range vacuum for guests**: the game's native 20 m reach with its own spring physics.
- **Guest inventory is kept**: your vacuum inventory is stored per host and restored when you rejoin.
- **Complete player FX**: the other player's suction cone and vac shockwave are visible, and their
  vacuum bends grass, flowers and plants on everyone's screen.
- **More slime behaviour**: Tangle vines and Tabby stalking are synced.
- **Shared progression**: treasure pods (state, animation, rewards and reward popups for everyone),
  blueprints, slime appearances, vac skins bought in the shop, market saturation and plort sales.
- **Per-player vac skins**: everyone picks their own skin at home, changeable mid-session, visible to all.
- **Host-authoritative shop, upgrades and currency**: no double purchases, no money desync.
- **Shared build-mode inventory**: gadget storage synced, including on join.
- **Ranch protection**: while connected, the game's cleanups never delete slimes, animals or plorts on
  your ranch (Tarr and produce keep vanilla rules).
- **No "chicken waterfall"**: wild animal spawning is capped while connected.
- **Gray Labyrinth & Sanctuary**: Prisma disruption, stabilizing field spawners, Sanctuary rooms and
  themes are synced.
- **Lighter on FPS**: expensive per-object tracing removed from player builds.

Full list: [CHANGELOG.md](CHANGELOG.md). Background: [docs/WHY-THIS-FORK.md](docs/WHY-THIS-FORK.md).

---

## Sync status

🟩 synced · 🟨 partially synced · 🟥 not synced

| Feature | Status | Details |
|:--|:--:|:--|
| Player movement, animations, sound FX | 🟩 | |
| Player visual FX | 🟩 | Remote suction cone, vac shockwave, grass and flowers bending under other players' vacuums (new in Lycoreia) |
| Player inventory | 🟩 | Guest inventory stored and restored on rejoin (new in Lycoreia) |
| Initial save load | 🟩 | Everything synced below is loaded on join |
| Slimes (incl. radiant) | 🟩 | Host-authoritative ownership |
| Slime behaviour | 🟩 | Yolky, Dervish, Sloomber, Gold, Lucky, **Tangle, Tabby** (new) and finally, 90% of Tarr sync! |
| Gordo slimes | 🟩 | Reward recovery added |
| Actors (items) | 🟩 | |
| Vacuum (pull, capture, throw) | 🟩 | Full native reach for guests (new in Lycoreia) |
| Resource nodes | 🟩 | |
| Landplots & upgrades | 🟩 | Gardens, silos, plort collectors, auto-feeders |
| Gadgets | 🟩 | Placing and picking up by any player |
| Build-mode inventory | 🟩 | new in Lycoreia |
| Drones | 🟩 | |
| Upgrades & shop | 🟩 | Host-authoritative purchases (new in Lycoreia) |
| Currency | 🟩 | Host-authoritative (new in Lycoreia) |
| Market prices & plort selling | 🟩 | Host-authoritative saturation (new in Lycoreia) |
| Treasure pods | 🟩 | State, animation, rewards, popups (new in Lycoreia) |
| Blueprints, appearances, vac skins | 🟩 | Host keeps the history (new in Lycoreia) |
| Sprinkles, refinery, world FX | 🟩 | |
| Slimepedia, map, map expansions | 🟩 | |
| Teleporters, geysers | 🟩 | Teleporter unlinking added |
| Weather, lightning, tornados, time | 🟩 | |
| Gray Labyrinth | 🟩 | Adds Prisma disruption and stabilizing field spawners (new in Lycoreia) |
| The Sanctuary | 🟩 | Adds room state and theme ownership (new in Lycoreia) |
| NPC conversations | 🟩 | |
| Multiplayer API | 🟩 | |
| Gardens (crop state) | 🟨 | Crops can occasionally differ between players |
| Tarr & water | 🟨 | Water effects on Tarr not shown to guests |
| Jetpack FX of other players | 🟥 | Planned |

---

## ⚠ Back up your saves

Lycoreia backs up your save automatically, but that is no substitute for your own backups.

### Save backups
Just before the game writes an autosave, the mod copies the loaded save's autosaves (including your
profile and settings files) into `UserData/Lycoreia/SaveBackups/`:

```
SaveBackups/
  2026-07-30_08-36-59/              <- when the backup was taken
    20260722122725_36/              <- the save
      beforehost/                   <- what you were doing at the time
        Steam/<your-steam-id>/      <- copy this folder into AppData/LocalLow/MonomiPark/SlimeRancher2/
          20260722122725_36_0.sav
          ...
```

| Phase | Taken when |
|---|---|
| `offline` | autosave in singleplayer |
| `beforehost` | the moment you open your world to others |
| `whilehost` | autosaves while you are hosting |
| `beforeconnect` | the moment before you connect, before anything is replaced |
| `whileconnect` | autosaves while you are connected |
| `afterdisconnect` | you left a host's session |
| `manual` | you ran the `backupsave` console command |

`beforehost`, `beforeconnect` and `afterdisconnect` always run, even with `save_backups` off, so joining
with the wrong save can always be undone (`save_backup_disable_forced` turns those off too).
Backups are never deleted unless you enable `save_backup_pruning` (keeps the newest
`save_backup_count`, default 10, per phase). Run `backupsave` in the console (F11) for a manual backup.

To restore: close the game and copy the `Steam/...` folder from the backup into
`AppData/LocalLow/MonomiPark/SlimeRancher2`, overwriting when asked.

---

## Install

### Requirements
- Slime Rancher 2 **1.3.0** (Steam)
- [MelonLoader 0.7.3](https://github.com/LavaGang/MelonLoader/releases)
- [Starlight 4.0.4](https://github.com/ThatFinnDev/Starlight/releases)

### Steps
1. Install **MelonLoader 0.7.3** onto Slime Rancher 2.
2. Put `Starlight.dll` into the game's `Mods` folder.
3. Download `Lycoreia-<version>.zip` from [Releases](https://github.com/rylaix/Lycoreia/releases) or [Nexus Mods](https://www.nexusmods.com/slimerancher2/mods/200) and
   copy `Mods/SR2MP.dll` into the game's `Mods` folder.
   **Replace Ranching Together**: it uses the same file name, and the two mods cannot run together.
4. Launch the game; the multiplayer menu appears on the main menu (F4 toggles it).
5. Fill in the first-time settings screen.

Every player needs **exactly the same version** of Lycoreia (and of every other mod).

### Hosting and joining
- **Host:** load the save you want to play on and start hosting.
- **Guest:** load an **empty save file** and always use that one to join this host.
  **When you join, all progress on the save you joined with is erased and replaced by the host's world.**
- Guests connect with the host's IP address (LAN IP on the same network; `127.0.0.1` is rejected) or a
  join code.

### Packages
- `Lycoreia-<version>.zip`: for playing.
- `Lycoreia-<version>-deeplog.zip`: writes detailed diagnostics to
  `UserData/Lycoreia/diagnostics.jsonl`. Use it when you want to report a bug.

### Coming from Ranching Together or an earlier Lycoreia test build
On first start, Lycoreia copies your old `UserData/SR2MP` folder (guest inventories, vac skin, save
backups) to `UserData/Lycoreia`. The old folder is left untouched.

---

## Configuration
Settings live in MelonPreferences (`UserData/MelonPreferences.cfg`) under the `SR2MP` category, and in
*ESC → Mods → Mod Settings*. Changes made in the multiplayer window are saved automatically.

---

## Reporting bugs
Open an [issue](https://github.com/rylaix/Lycoreia/issues/new/choose) and attach `latest.log` and
`diagnostics.jsonl*` from `UserData/Lycoreia/` of **both** players (use the `-deeplog` package while
reproducing). Logs can contain IP addresses, so check them before posting.

---

## Building from source
See [docs/BUILDING.md](docs/BUILDING.md). Releases are built by GitHub Actions
(**Actions → Build release → Run workflow**).

---

## License & credits
Lycoreia is free software under the **GNU General Public License v3**; see [LICENSE](LICENSE).

- **Core developer:** **Rylaix**, <https://github.com/rylaix>
- **Core QA:** **Kum1te**, <https://github.com/kum1te>
- Lycoreia started as a fork of [Ranching Together](https://github.com/pyeight/SlimeRancher2Multiplayer)
  (about 15 % of that code remains); its authors and other upstream credits are listed in
  [NOTICE.md](NOTICE.md).

Slime Rancher 2 is a trademark of Monomi Park. This project is not affiliated with or endorsed by
Monomi Park and contains no game files.
