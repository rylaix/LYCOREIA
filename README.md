# Lycoreia

**Host-authoritative multiplayer co-op for Slime Rancher 2.**
Made by **[Rylaix](https://github.com/rylaix)** (core developer) and **[Kum1te](https://github.com/kum1te)** (core QA).

[Download](https://github.com/rylaix/Lycoreia/releases/latest) ·
[Nexus Mods](https://www.nexusmods.com/slimerancher2/mods/200) ·
[Discord](https://discord.gg/EvD7WhSwqh) ·
[Report a bug](https://github.com/rylaix/Lycoreia/issues/new/choose) ·
[Changelog](CHANGELOG.md)

> Lycoreia was the town on Mount Parnassus founded by the survivors of Deucalion's flood.
> This project is the ranch rebuilt on higher ground.

---

## Testing now: 2.1.0-rc1

A release candidate of 2.1.0 (co-op board, co-op wheel, boost jump and many sync fixes) is out as a diagnostic build. Grab `Lycoreia-2.1.0-rc1.zip` from the Releases page, play with a friend (both need rc1), and send `UserData/Lycoreia/latest.log` and `diagnostics.jsonl` from both players on Discord. See the [changelog](CHANGELOG.md).

---

## Lycoreia 2.0.0

2.0.0 is a ground-up rewrite: new networking, sessions, world and actor sync, avatars and menus, built
and tested as one system. It installs as a plain MelonLoader mod and needs nothing else.

Two or more players share one Slime Rancher 2 world. The **host** owns the save and is the single
source of truth; guests join over LAN or the internet and see the same ranch, slimes, items and
progress.

### Highlights
- **Reliable netcode**: acknowledged, ordered and resent messages, compression and checksums, a separate
  lane for movement, and clean recovery when a player drops or rejoins.
- **Easy hosting**: join codes, automatic port mapping (UPnP and NAT-PMP), LAN or direct IP.
- **One world, one truth**: every slime, plort and item has exactly one owner; the host confirms
  captures, so two players grabbing the same slime never split the world.
- **Shared progression**: time and weather, switches, doors and puzzles, map fog and map nodes, the
  Slimepedia, gardens, drones, Gordos, treasure pods, refinery, market and plort sales, shop, upgrades
  (shared by everyone), teleporters, decorations, gadgets and world events.
- **Your ranch is safe**: while connected, the game's cleanups and despawns never delete slimes on your
  ranch and its expansions.
- **Real remote players**: animated avatars with look, vacuum hold and kicks, nametags, pings, chat,
  markers on the map, radar and compass, and their vacuum effects.
- **Built-in Discord Rich Presence** on Windows and on Linux through Proton.
- **Own in-game menu** for hosting, joining, player colour, settings and widescreen FOV.

Everything that changed is in the [changelog](CHANGELOG.md).

---

## Install

1. Install [MelonLoader](https://github.com/LavaGang/MelonLoader) **0.7.x** for Slime Rancher 2 and start
   the game once.
2. Download `Lycoreia.dll` from the [latest release](https://github.com/rylaix/Lycoreia/releases/latest)
   and put it in the game's `Mods` folder.
3. **Every player** needs the same Lycoreia version. A player on a different build is refused with a
   readable message.

**Upgrading from 1.x:** remove `Mods/Starlight.dll`, `UserLibs/SharpOpenNat.dll`,
`UserLibs/DiscordRPC.dll` and the old `UserData/Lycoreia/*.lic.txt` files. 2.0.0 cannot play with 1.x.

Linux: works through Proton (tested on GE-Proton 10), including Discord presence with the native Linux
Discord client (regular, Flatpak, Snap or Vesktop).

## Play
- **Host:** open the **Multiplayer** button (main menu or pause menu) → *Host*. With automatic hosting
  you get a join code to share; manual hosting needs the port forwarded on your router.
- **Join:** Multiplayer → *Join*, enter the join code or the host's IP and port.
- Save backups are made automatically when you host and when someone joins
  (`UserData/Lycoreia/SaveBackups`). Keep your own backups too.

## Reporting bugs
Use the [bug report form](https://github.com/rylaix/Lycoreia/issues/new/choose) and attach logs from
**both** players: `UserData/Lycoreia/latest.log` and `diagnostics.jsonl` (use the `-deeplog` package
from the release page to get detailed diagnostics). Logs can contain IP addresses and player IDs;
replace your public IP with `x.x.x.x` before posting if you prefer.

Questions and help: [Discord](https://discord.gg/EvD7WhSwqh).

## Contributing
Pull requests are welcome; see [CONTRIBUTING.md](CONTRIBUTING.md) and [docs/BUILDING.md](docs/BUILDING.md).

## License
Lycoreia is **source-available, not open source**. You may use official releases to play, read the
code, report bugs and send pull requests. You may **not** copy, redistribute or re-upload it, build
other mods or projects from it, or use its name. See [LICENSE](LICENSE).

Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.

Slime Rancher 2 is a trademark of Monomi Park. Lycoreia is an unofficial fan-made mod and is not
affiliated with or endorsed by Monomi Park.
